//! macOS: the AudioQueue microphone input behind `prisc_audio_input_*`.
//!
//! An AudioToolbox input queue (`AudioQueueNewInput`, no run loop, so buffers arrive on the queue's
//! own thread) pointed at one CoreAudio device through `kAudioQueueProperty_CurrentDevice`, asked
//! for interleaved float32 at the requested rate (the queue converts from the device's rate) in the
//! device's own channel count. The default input is resolved to a concrete device at open and
//! pinned the same way, so a take stays on the microphone it started on.
//!
//! Device loss is not something the queue reports; it just stops calling. A HAL listener on the
//! device's `kAudioDevicePropertyDeviceIsAlive` turns an unplug into a Stopped event, from the
//! HAL's notification thread.
//!
//! Lifetime: the OS holds a token, not a pointer. Both trampolines look the token up in a registry
//! and hold an `Arc` of the state for the length of the callback; destroy removes the token first,
//! so a late callback finds nothing. Every host invocation runs under the `deliver` lock with the
//! `closed` / `ended` flags checked inside it, and destroy takes that lock once after setting
//! `closed`, which is how it waits out a callback in flight (and drains the listener: behaviour
//! change 5.2.9).

use std::cell::Cell;
use std::collections::HashMap;
use std::ffi::c_void;
use std::mem;
use std::ptr::{self, NonNull};
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};

use objc2_audio_toolbox::{
    kAudioQueueProperty_CurrentDevice, AudioQueueAllocateBuffer, AudioQueueBufferRef, AudioQueueDispose, AudioQueueEnqueueBuffer,
    AudioQueueNewInput, AudioQueueRef, AudioQueueSetProperty, AudioQueueStart, AudioQueueStop, OpaqueAudioQueue,
};
use objc2_av_foundation::AVAuthorizationStatus;
use objc2_core_audio::{
    kAudioDevicePropertyDeviceIsAlive, kAudioDevicePropertyDeviceUID, kAudioObjectPropertyName, kAudioObjectPropertyScopeGlobal,
    AudioObjectAddPropertyListener, AudioObjectID, AudioObjectPropertyAddress, AudioObjectRemovePropertyListener,
};
use objc2_core_audio_types::{AudioStreamPacketDescription, AudioTimeStamp};
use objc2_core_foundation::{CFRetained, CFString};

use crate::error::{Error, Result};
use crate::ffi::audio_input::{PriscAudioInput, PriscAudioInputEvent, PriscAudioInputEventKind, PriscAudioInputInfo, PriscAudioInputStop};
use crate::ffi::log::{self, PriscInteropLogLevel};
use crate::ffi::types::PriscString;
use crate::panic::{self, CallbackPanic};
use crate::platform::macos::devices::{self, coreaudio_error, float32_interleaved};

/// The host's callback, as the export receives it.
pub(crate) type InputFn = unsafe extern "C" fn(user: *mut c_void, event: *const PriscAudioInputEvent) -> u8;

/// Buffers in flight: one being filled, one on its way to the host, one spare so a slow callback
/// costs no audio.
const BUFFER_COUNT: usize = 3;

/// The ceiling on one buffer, so the size arithmetic cannot overflow from a caller argument.
const MAX_BUFFER_BYTES: u64 = 64 << 20;

thread_local! {
    /// Set while this thread is inside a host invocation, so a destroy from inside the callback is
    /// refused instead of deadlocking on the `deliver` lock it would wait for.
    static IN_CALLBACK: Cell<bool> = const { Cell::new(false) };
}

/// One open input. Shared (`Arc`) between the handle and the trampolines that are running.
pub(crate) struct Input {
    token: usize,
    queue: AtomicPtr<OpaqueAudioQueue>,
    device: AudioObjectID,
    callback: InputFn,
    user: *mut c_void,
    name: String,
    channels: u32,
    sample_rate_hz: u32,
    used_default: bool,
    /// The UID string the queue was pointed at, kept for the queue's lifetime.
    device_uid: CFRetained<CFString>,
    /// Delivery has ended: request_stop, a callee-requested end, or a Stopped event.
    ended: AtomicBool,
    /// Destroy has begun: no host invocation may start.
    closed: AtomicBool,
    started: AtomicBool,
    listening: AtomicBool,
    /// Held for every host invocation; destroy takes it once after setting `closed`.
    deliver: Mutex<()>,
    /// Where the trampolines record a caught panic; the handle's next export reports it.
    panic: CallbackPanic,
}

// `user` is the host's opaque pointer, handed back only on the OS audio threads under `deliver`;
// the queue is atomic; everything else is immutable after open or atomic.
unsafe impl Send for Input {}
unsafe impl Sync for Input {}

/// The live inputs by token.
fn registry() -> &'static Mutex<HashMap<usize, Arc<Input>>> {
    static LIVE: OnceLock<Mutex<HashMap<usize, Arc<Input>>>> = OnceLock::new();
    LIVE.get_or_init(|| Mutex::new(HashMap::new()))
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
}

/// The input behind a token, if it is still registered. The registry lock is released before the
/// caller does anything with it.
fn lookup(token: usize) -> Option<Arc<Input>> {
    lock(registry()).get(&token).cloned()
}

fn next_token() -> usize {
    static NEXT: AtomicUsize = AtomicUsize::new(1);
    NEXT.fetch_add(1, Ordering::Relaxed)
}

/// The device's is-alive flag, the property the device-loss listener watches.
fn alive_address() -> AudioObjectPropertyAddress {
    devices::address(kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal)
}

/// The queue's thread: one filled buffer.
unsafe extern "C-unwind" fn input_trampoline(
    user: *mut c_void,
    queue: AudioQueueRef,
    buffer: AudioQueueBufferRef,
    _start: NonNull<AudioTimeStamp>,
    _packets: u32,
    _descriptions: *const AudioStreamPacketDescription,
) {
    let input = panic::callback_guard(CallbackPanic::process(), None, || lookup(user as usize));
    if let Some(input) = input {
        panic::callback_guard(&input.panic, (), || input.on_buffer(queue, buffer));
    }
}

/// The HAL's notification thread: the device's is-alive flag changed.
unsafe extern "C-unwind" fn alive_trampoline(
    _object: AudioObjectID,
    _count: u32,
    _addresses: NonNull<AudioObjectPropertyAddress>,
    client: *mut c_void,
) -> i32 {
    let input = panic::callback_guard(CallbackPanic::process(), None, || lookup(client as usize));
    if let Some(input) = input {
        panic::callback_guard(&input.panic, (), || input.on_alive_changed());
    }
    0
}

impl Input {
    /// Opens the input: authorization, device, format, queue, three buffers. Nothing is left open
    /// on failure.
    pub(crate) fn open(uid: &str, sample_rate_hz: u32, buffer_ms: u32, callback: InputFn, user: *mut c_void) -> Result<Arc<Input>> {
        let status = devices::authorization(true);
        if status == AVAuthorizationStatus::Denied || status == AVAuthorizationStatus::Restricted {
            return Err(Error::permission(
                "Microphone access is denied for Prisc (System Settings > Privacy & Security > Microphone).",
            ));
        }

        let (device, used_default) = match devices::device_for_uid(uid) {
            Some(device) => (device, false),
            None => (
                devices::default_device(false).ok_or_else(|| Error::capability("There is no default microphone."))?,
                true,
            ),
        };
        let device_uid = devices::string_property(device, kAudioDevicePropertyDeviceUID)
            .filter(|uid| !uid.is_empty())
            .ok_or_else(|| Error::capability("The microphone has no device UID."))?;
        let name = devices::string_property(device, kAudioObjectPropertyName).unwrap_or_else(|| device_uid.clone());
        let channels = devices::input_channels(device).max(1);

        let frames = (u64::from(sample_rate_hz) * u64::from(buffer_ms) / 1000).max(1);
        let buffer_bytes = frames * u64::from(channels) * mem::size_of::<f32>() as u64;
        if buffer_bytes > MAX_BUFFER_BYTES {
            return Err(Error::invalid_arg(format!(
                "a {buffer_ms} ms buffer of {channels} channels at {sample_rate_hz} Hz is too large"
            )));
        }

        let input = Arc::new(Input {
            token: next_token(),
            queue: AtomicPtr::new(ptr::null_mut()),
            device,
            callback,
            user,
            name,
            channels,
            sample_rate_hz,
            used_default,
            device_uid: CFString::from_str(&device_uid),
            ended: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            started: AtomicBool::new(false),
            listening: AtomicBool::new(false),
            deliver: Mutex::new(()),
            panic: CallbackPanic::new(),
        });
        lock(registry()).insert(input.token, Arc::clone(&input));
        if let Err(e) = input.create_queue(buffer_bytes as u32) {
            input.teardown();
            return Err(e);
        }
        Ok(input)
    }

    fn create_queue(&self, buffer_bytes: u32) -> Result<()> {
        let mut format = float32_interleaved(self.sample_rate_hz, self.channels);
        let mut queue: AudioQueueRef = ptr::null_mut();
        // SAFETY: the format and out pointer are locals; the user data is the token, never a
        // pointer; no run loop, so callbacks come on the queue's own thread.
        let status = unsafe {
            AudioQueueNewInput(
                NonNull::from(&mut format),
                Some(input_trampoline),
                self.token as *mut c_void,
                None,
                None,
                0,
                NonNull::from(&mut queue),
            )
        };
        if status != 0 {
            return Err(coreaudio_error(status, "creating the input queue"));
        }
        self.queue.store(queue, Ordering::Release);

        let uid: *const CFString = &*self.device_uid;
        // SAFETY: the property value is one CFStringRef, which `device_uid` keeps alive.
        let status = unsafe {
            AudioQueueSetProperty(
                queue,
                kAudioQueueProperty_CurrentDevice,
                NonNull::from(&uid).cast(),
                mem::size_of::<*const CFString>() as u32,
            )
        };
        if status != 0 {
            return Err(coreaudio_error(status, &format!("selecting the microphone '{}'", self.name)));
        }
        for _ in 0..BUFFER_COUNT {
            let mut buffer: AudioQueueBufferRef = ptr::null_mut();
            // SAFETY: a live queue and a local out pointer.
            let status = unsafe { AudioQueueAllocateBuffer(queue, buffer_bytes, NonNull::from(&mut buffer)) };
            if status != 0 {
                return Err(coreaudio_error(status, "allocating a capture buffer"));
            }
            // SAFETY: a buffer of this queue; no packet descriptions for linear PCM.
            let status = unsafe { AudioQueueEnqueueBuffer(queue, buffer, 0, ptr::null()) };
            if status != 0 {
                return Err(coreaudio_error(status, "queueing a capture buffer"));
            }
        }
        Ok(())
    }

    /// What the export reports; `name` is borrowed from this input (mode 3).
    pub(crate) fn info(&self) -> PriscAudioInputInfo {
        PriscAudioInputInfo {
            channels: self.channels,
            sample_rate_hz: self.sample_rate_hz,
            used_default: u8::from(self.used_default),
            _reserved: [0; 7],
            name: if self.name.is_empty() {
                PriscString::empty()
            } else {
                PriscString {
                    ptr: self.name.as_ptr().cast_mut(),
                    len: self.name.len(),
                }
            },
        }
    }

    pub(crate) fn into_handle(input: Arc<Input>) -> *mut PriscAudioInput {
        Box::into_raw(Box::new(input)).cast()
    }

    /// The input behind a live handle; `PRISC_E_INVALID_ARG` for null, `PRISC_E_PANIC` when a
    /// trampoline caught a panic since the last call.
    ///
    /// # Safety
    /// `handle` is null or came from `prisc_audio_input_open` and was not destroyed.
    pub(crate) unsafe fn live<'a>(handle: *mut PriscAudioInput) -> Result<&'a Input> {
        let input = handle
            .cast::<Arc<Input>>()
            .as_ref()
            .ok_or_else(|| Error::null("input"))?;
        input.panic.check()?;
        Ok(input)
    }

    /// Starts the queue (idempotent) and the device-alive listener; answers whether the listener
    /// is on.
    pub(crate) fn start(&self) -> Result<bool> {
        if self.ended.load(Ordering::Acquire) {
            return Err(Error::state("the microphone input has already stopped"));
        }
        if self.started.load(Ordering::Acquire) {
            return Ok(self.listening.load(Ordering::Acquire));
        }
        // SAFETY: the queue this input owns; no start time means "now".
        let status = unsafe { AudioQueueStart(self.queue.load(Ordering::Acquire), ptr::null()) };
        if status != 0 {
            return Err(coreaudio_error(status, &format!("starting the microphone '{}'", self.name)));
        }
        self.started.store(true, Ordering::Release);

        let mut address = alive_address();
        // SAFETY: the client data is the token; the listener is removed in teardown.
        let status = unsafe {
            AudioObjectAddPropertyListener(
                self.device,
                NonNull::from(&mut address),
                Some(alive_trampoline),
                self.token as *mut c_void,
            )
        };
        let watching = status == 0;
        if !watching {
            log::emit(
                PriscInteropLogLevel::Warn,
                &coreaudio_error(status, "watching the microphone for removal").message,
            );
        }
        self.listening
            .store(watching, Ordering::Release);
        Ok(watching)
    }

    /// Ends delivery; true for the call that ended it. Takes no lock, so it is safe anywhere.
    pub(crate) fn request_stop(&self) -> bool {
        !self.ended.swap(true, Ordering::AcqRel)
    }

    /// Invokes the host with `event`. The caller holds `deliver` and checked `closed`.
    fn invoke(&self, event: &PriscAudioInputEvent) -> bool {
        let _ = IN_CALLBACK.try_with(|flag| flag.set(true));
        // SAFETY: the host's callback with its own user pointer; the event lives on our stack for
        // the call.
        let keep = unsafe { (self.callback)(self.user, event) } != 0;
        let _ = IN_CALLBACK.try_with(|flag| flag.set(false));
        keep
    }

    fn may_deliver(&self) -> bool {
        !self.closed.load(Ordering::Acquire) && !self.ended.load(Ordering::Acquire)
    }

    /// Hands one filled buffer to the host, then back to the queue unless delivery ended.
    fn on_buffer(&self, queue: AudioQueueRef, buffer: AudioQueueBufferRef) {
        let _deliver = lock(&self.deliver);
        if !self.may_deliver() || queue != self.queue.load(Ordering::Acquire) || buffer.is_null() {
            return;
        }
        // SAFETY: the queue hands us one of its own buffers for the duration of the callback.
        let (data, bytes) = unsafe { ((*buffer).mAudioData, (*buffer).mAudioDataByteSize) };
        if bytes > 0 {
            let event = PriscAudioInputEvent {
                kind: PriscAudioInputEventKind::Data as u32,
                data_len: bytes,
                data: data.as_ptr().cast_const().cast(),
                stop: 0,
                os_status: 0,
            };
            if !self.invoke(&event) {
                // A callee-requested end: no Stopped follows.
                self.ended.store(true, Ordering::Release);
                return;
            }
        }
        // A request_stop on another thread during the callback makes this the last buffer.
        if !self.may_deliver() {
            return;
        }
        // SAFETY: the buffer goes back to the queue it came from.
        let status = unsafe { AudioQueueEnqueueBuffer(queue, buffer, 0, ptr::null()) };
        if status != 0 && !self.ended.swap(true, Ordering::AcqRel) {
            self.invoke(&PriscAudioInputEvent {
                kind: PriscAudioInputEventKind::Stopped as u32,
                data_len: 0,
                data: ptr::null(),
                stop: PriscAudioInputStop::RequeueFailed as i32,
                os_status: status,
            });
        }
    }

    /// A device that is gone does not answer either, which is the same news.
    fn on_alive_changed(&self) {
        let alive = devices::u32_property(self.device, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal).unwrap_or(0);
        if alive != 0 {
            return;
        }
        let _deliver = lock(&self.deliver);
        if self.closed.load(Ordering::Acquire) || self.ended.swap(true, Ordering::AcqRel) {
            return;
        }
        self.invoke(&PriscAudioInputEvent {
            kind: PriscAudioInputEventKind::Stopped as u32,
            data_len: 0,
            data: ptr::null(),
            stop: PriscAudioInputStop::DeviceLost as i32,
            os_status: 0,
        });
    }

    /// Closes everything this input opened, in the order that keeps the host safe: no new host
    /// invocation (closed), no new lookup (unregistered), no listener, wait out an invocation in
    /// flight (the `deliver` lock), and only then stop and dispose the queue.
    fn teardown(&self) {
        self.closed.store(true, Ordering::Release);
        self.ended.store(true, Ordering::Release);
        lock(registry()).remove(&self.token);
        if self.listening.swap(false, Ordering::AcqRel) {
            let mut address = alive_address();
            // SAFETY: the same (device, address, proc, client data) the listener was added with.
            let status = unsafe {
                AudioObjectRemovePropertyListener(
                    self.device,
                    NonNull::from(&mut address),
                    Some(alive_trampoline),
                    self.token as *mut c_void,
                )
            };
            if status != 0 {
                log::emit(
                    PriscInteropLogLevel::Warn,
                    &coreaudio_error(status, "no longer watching the microphone").message,
                );
            }
        }
        drop(lock(&self.deliver));
        let queue = self
            .queue
            .swap(ptr::null_mut(), Ordering::AcqRel);
        if !queue.is_null() {
            // Both immediate: stop synchronously, then free the queue and its buffers. On a device
            // that was just unplugged these report errors, which is fine.
            // SAFETY: the queue this input owns, freed exactly once (the swap above).
            let statuses = unsafe { [AudioQueueStop(queue, true), AudioQueueDispose(queue, true)] };
            if let Some(status) = statuses.into_iter().find(|&s| s != 0) {
                log::emit(
                    PriscInteropLogLevel::Debug,
                    &coreaudio_error(status, "closing the microphone").message,
                );
            }
        }
    }

    /// The handle's destroy: refuses from inside the callback, otherwise tears down and frees the
    /// handle (the state itself goes when the last running trampoline lets go of it).
    ///
    /// # Safety
    /// `handle` came from `prisc_audio_input_open` and was not destroyed.
    pub(crate) unsafe fn destroy(handle: *mut PriscAudioInput) -> Result<()> {
        if IN_CALLBACK
            .try_with(Cell::get)
            .unwrap_or(false)
        {
            return Err(Error::state(
                "prisc_audio_input_destroy must not be called from inside the input callback",
            ));
        }
        let input = *Box::from_raw(handle.cast::<Arc<Input>>());
        input.teardown();
        if let Some(text) = input.panic.take() {
            log::emit(PriscInteropLogLevel::Error, &format!("the microphone input callback: {text}"));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;
    use std::sync::atomic::{AtomicI32, AtomicU32};
    use std::thread::sleep;
    use std::time::{Duration, Instant};

    /// What a test's callback saw, reached through `user`; leaked because a late assertion reads it
    /// after destroy, and per test because cargo runs the tests in parallel.
    #[derive(Default)]
    struct Seen {
        data: AtomicU32,
        bytes: AtomicU32,
        stopped: AtomicU32,
        /// When non-zero, the callback answers 0 (end) on this Data event.
        end_at: AtomicU32,
        /// When set, the callback takes it on its next Data event, calls the destroy export on it
        /// from inside the callback and records the status in `destroy_status` (`tried` says it ran).
        destroy_in_callback: AtomicPtr<PriscAudioInput>,
        destroy_status: AtomicI32,
        tried: AtomicBool,
    }

    fn seen() -> &'static Seen {
        Box::leak(Box::default())
    }

    unsafe extern "C" fn record(user: *mut c_void, event: *const PriscAudioInputEvent) -> u8 {
        let seen = &*user.cast::<Seen>();
        let event = &*event;
        if event.kind == PriscAudioInputEventKind::Data as u32 {
            let n = seen.data.fetch_add(1, Ordering::AcqRel) + 1;
            seen.bytes
                .fetch_add(event.data_len, Ordering::AcqRel);
            let handle = seen
                .destroy_in_callback
                .swap(ptr::null_mut(), Ordering::AcqRel);
            if !handle.is_null() {
                let status = crate::ffi::audio_input::prisc_audio_input_destroy(handle);
                seen.destroy_status
                    .store(status, Ordering::Release);
                seen.tried.store(true, Ordering::Release);
            }
            let end_at = seen.end_at.load(Ordering::Acquire);
            return u8::from(end_at == 0 || n < end_at);
        }
        seen.stopped.fetch_add(1, Ordering::AcqRel);
        0
    }

    fn user(seen: &'static Seen) -> *mut c_void {
        ptr::from_ref(seen).cast_mut().cast()
    }

    /// Opens the default input, or `None` (with the reason) where the test cannot: no grant on this
    /// machine (asserting the documented answer), or no input device.
    fn open_default(seen: &'static Seen) -> Option<Arc<Input>> {
        let status = devices::authorization(true);
        if status != AVAuthorizationStatus::Authorized {
            if status == AVAuthorizationStatus::Denied || status == AVAuthorizationStatus::Restricted {
                let e = Input::open("", 48_000, 50, record, user(seen))
                    .err()
                    .expect("a denied open");
                assert_eq!(e.status, crate::ffi::status::PriscInteropStatus::Permission);
            }
            eprintln!("skipped: no microphone grant for this process ({status:?})");
            return None;
        }
        match Input::open("", 48_000, 50, record, user(seen)) {
            Ok(input) => Some(input),
            Err(e) if e.status == crate::ffi::status::PriscInteropStatus::Capability => {
                eprintln!("skipped: {}", e.message);
                None
            }
            Err(e) => panic!("open failed: {:?} {}", e.status, e.message),
        }
    }

    fn wait(condition: impl Fn() -> bool) -> bool {
        let deadline = Instant::now() + Duration::from_secs(3);
        while Instant::now() < deadline {
            if condition() {
                return true;
            }
            // A coarse latch on a test thread, not a precise wait.
            sleep(Duration::from_millis(10));
        }
        false
    }

    fn close(input: Arc<Input>) {
        let handle = Input::into_handle(input);
        unsafe { Input::destroy(handle) }.expect("destroy");
    }

    #[test]
    fn the_default_input_delivers_float_data_until_request_stop() {
        let seen = seen();
        let Some(input) = open_default(seen) else { return };
        assert!(input.used_default);
        assert!(input.channels >= 1);
        assert!(!input.name.is_empty());
        assert_eq!(seen.data.load(Ordering::Acquire), 0, "data before start");
        input.start().expect("start");
        assert!(wait(|| seen.data.load(Ordering::Acquire) >= 2), "no data within 3 s");
        // Whole float32 frames in every buffer.
        assert_eq!(seen.bytes.load(Ordering::Acquire) % (4 * input.channels), 0);

        assert!(input.request_stop(), "the first request ends delivery");
        assert!(!input.request_stop(), "a second request reports it was not first");
        sleep(Duration::from_millis(150));
        let latched = seen.data.load(Ordering::Acquire);
        sleep(Duration::from_millis(250));
        assert_eq!(seen.data.load(Ordering::Acquire), latched, "data after request_stop");
        assert_eq!(seen.stopped.load(Ordering::Acquire), 0, "request_stop never invokes the callback");
        assert!(input.start().is_err(), "a stopped input does not start again");
        close(input);
    }

    #[test]
    fn a_callee_requested_end_stops_delivery_without_a_stopped_event() {
        let seen = seen();
        seen.end_at.store(1, Ordering::Release);
        let Some(input) = open_default(seen) else { return };
        input.start().expect("start");
        assert!(wait(|| seen.data.load(Ordering::Acquire) >= 1), "no data within 3 s");
        sleep(Duration::from_millis(300));
        assert_eq!(seen.data.load(Ordering::Acquire), 1, "data after the callee ended delivery");
        assert_eq!(seen.stopped.load(Ordering::Acquire), 0);
        assert!(!input.request_stop(), "delivery had already ended");
        close(input);
    }

    #[test]
    fn destroy_after_open_without_start_and_after_a_failed_start() {
        let seen = seen();
        let Some(input) = open_default(seen) else { return };
        close(input);
        let Some(input) = open_default(seen) else { return };
        input.request_stop();
        assert!(input.start().is_err(), "start after the end fails");
        close(input);
        assert_eq!(seen.data.load(Ordering::Acquire), 0);
    }

    #[test]
    fn a_late_os_callback_after_destroy_finds_nothing() {
        let seen = seen();
        let Some(input) = open_default(seen) else { return };
        let token = input.token;
        close(input);
        assert!(lookup(token).is_none());
        // What the HAL would do with a stale client datum: nothing reaches the host.
        let mut address = alive_address();
        let status = unsafe { alive_trampoline(0, 1, NonNull::from(&mut address), token as *mut c_void) };
        assert_eq!(status, 0);
        assert_eq!(seen.stopped.load(Ordering::Acquire), 0);
    }

    #[test]
    fn an_unknown_uid_falls_back_to_the_default() {
        let seen = seen();
        if devices::authorization(true) != AVAuthorizationStatus::Authorized {
            return;
        }
        match Input::open("prisc-no-such-microphone", 48_000, 50, record, user(seen)) {
            Ok(input) => {
                assert!(input.used_default);
                close(input);
            }
            Err(e) => assert_eq!(e.status, crate::ffi::status::PriscInteropStatus::Capability, "{}", e.message),
        }
    }

    /// A private aggregate device over the default input, destroyed mid-capture: the is-alive
    /// listener turns its disappearance into exactly one Stopped(DeviceLost), delivery ends, and a
    /// destroy afterwards is clean. This is the unplug path, without unplugging anything.
    #[test]
    fn losing_the_device_raises_one_stopped_device_lost() {
        use objc2_core_audio::{
            kAudioAggregateDeviceIsPrivateKey, kAudioAggregateDeviceNameKey, kAudioAggregateDeviceSubDeviceListKey,
            kAudioAggregateDeviceUIDKey, kAudioSubDeviceUIDKey, AudioHardwareCreateAggregateDevice, AudioHardwareDestroyAggregateDevice,
        };
        use objc2_core_foundation::{CFArray, CFDictionary, CFNumber, CFType};

        if devices::authorization(true) != AVAuthorizationStatus::Authorized {
            eprintln!("skipped: no microphone grant");
            return;
        }
        let Some(sub_uid) = devices::default_device(false).and_then(|d| devices::string_property(d, kAudioDevicePropertyDeviceUID)) else {
            eprintln!("skipped: no default input");
            return;
        };
        let key = |k: &std::ffi::CStr| CFString::from_str(k.to_str().unwrap());
        let sub_uid = CFString::from_str(&sub_uid);
        let sub = CFDictionary::<CFString, CFType>::from_slices(&[&*key(kAudioSubDeviceUIDKey)], &[sub_uid.as_ref()]);
        let subs = CFArray::from_objects(&[&*sub]);
        let uid_text = format!("com.prisc.interop-test.lost.{}", std::process::id());
        let uid = CFString::from_str(&uid_text);
        let name = CFString::from_str("Prisc interop test (device loss)");
        let private = CFNumber::new_i32(1);
        let keys = [
            key(kAudioAggregateDeviceUIDKey),
            key(kAudioAggregateDeviceNameKey),
            key(kAudioAggregateDeviceSubDeviceListKey),
            key(kAudioAggregateDeviceIsPrivateKey),
        ];
        let description = CFDictionary::<CFString, CFType>::from_slices(
            &keys.iter().map(|k| &**k).collect::<Vec<_>>(),
            &[uid.as_ref(), name.as_ref(), subs.as_ref(), private.as_ref()],
        );
        let mut aggregate: AudioObjectID = 0;
        let status = unsafe { AudioHardwareCreateAggregateDevice(description.as_ref(), NonNull::from(&mut aggregate)) };
        if status != 0 {
            eprintln!("skipped: the HAL would not create an aggregate device ({status})");
            return;
        }
        // The HAL publishes a new aggregate asynchronously.
        assert!(
            wait(|| devices::device_for_uid(&uid_text) == Some(aggregate)),
            "the aggregate never appeared"
        );

        let seen = seen();
        let input = Input::open(&uid_text, 48_000, 50, record, user(seen)).expect("open the aggregate");
        assert!(!input.used_default, "the aggregate's UID must resolve");
        assert!(input.start().expect("start"), "the is-alive watch must be on");
        assert!(wait(|| seen.data.load(Ordering::Acquire) >= 1), "no data from the aggregate");

        assert_eq!(unsafe { AudioHardwareDestroyAggregateDevice(aggregate) }, 0);
        assert!(
            wait(|| seen.stopped.load(Ordering::Acquire) >= 1),
            "no Stopped after the device went away"
        );
        sleep(Duration::from_millis(300));
        assert_eq!(seen.stopped.load(Ordering::Acquire), 1, "Stopped fires at most once");
        let latched = seen.data.load(Ordering::Acquire);
        sleep(Duration::from_millis(200));
        assert_eq!(seen.data.load(Ordering::Acquire), latched, "data after Stopped");
        assert!(!input.request_stop(), "delivery had already ended");
        close(input);
    }

    /// The destroy export from inside the callback is refused with `PRISC_E_STATE` and destroys
    /// nothing (it would otherwise wait on the `deliver` lock its own thread holds): data keeps
    /// coming, and a destroy from the test thread afterwards succeeds.
    #[test]
    fn destroy_from_inside_the_callback_is_refused_and_destroys_nothing() {
        let seen = seen();
        let Some(input) = open_default(seen) else { return };
        let handle = Input::into_handle(input);
        // SAFETY: the handle is live until the destroy at the end of this test.
        let input = unsafe { Input::live(handle) }.expect("live");
        seen.destroy_in_callback
            .store(handle, Ordering::Release);
        input.start().expect("start");
        assert!(wait(|| seen.tried.load(Ordering::Acquire)), "no data within 3 s");
        assert_eq!(seen.destroy_status.load(Ordering::Acquire), PriscInteropStatus::State as i32);
        let after = seen.data.load(Ordering::Acquire);
        assert!(
            wait(|| seen.data.load(Ordering::Acquire) > after),
            "the refused destroy stopped delivery"
        );
        assert!(unsafe { Input::live(handle) }.is_ok(), "the handle survived the refused destroy");
        assert_eq!(
            unsafe { crate::ffi::audio_input::prisc_audio_input_destroy(handle) },
            PriscInteropStatus::Ok as i32
        );
    }

    #[test]
    fn an_oversized_buffer_is_refused_before_anything_opens() {
        let seen = seen();
        if devices::authorization(true) != AVAuthorizationStatus::Authorized || devices::default_device(false).is_none() {
            return;
        }
        let e = Input::open("", 192_000, u32::MAX, record, user(seen))
            .err()
            .expect("refused");
        assert_eq!(e.status, crate::ffi::status::PriscInteropStatus::InvalidArg);
    }
}
