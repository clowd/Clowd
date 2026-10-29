//! macOS: the default-output AudioUnit behind `prisc_audio_output_*`.
//!
//! The unit pulls interleaved float32 through [`render_trampoline`] on CoreAudio's own IO thread,
//! the one thread running this code that the host did not call on. That trampoline logs nothing,
//! takes no lock and allocates nothing; a panic in it is caught by `callback_guard`, the buffer is
//! left silent, and the next export on the handle reports it.
//!
//! Nothing here touches a device volume or the device's buffer size: attenuation is the caller's
//! job, and moving the device's buffer would put the latency the caller corrects by out of step.

use std::ffi::c_void;
use std::mem;
use std::ptr::{self, NonNull};
use std::sync::atomic::{AtomicBool, Ordering};

use objc2_audio_toolbox::{
    kAudioUnitManufacturer_Apple, kAudioUnitProperty_SetRenderCallback, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input,
    kAudioUnitScope_Output, kAudioUnitSubType_DefaultOutput, kAudioUnitType_Output, AURenderCallbackStruct, AudioComponentDescription,
    AudioComponentFindNext, AudioComponentInstance, AudioComponentInstanceDispose, AudioComponentInstanceNew, AudioOutputUnitStart,
    AudioOutputUnitStop, AudioUnitGetProperty, AudioUnitInitialize, AudioUnitRenderActionFlags, AudioUnitSetProperty,
    AudioUnitUninitialize,
};
use objc2_core_audio::{kAudioDevicePropertyBufferFrameSize, kAudioDevicePropertyLatency, kAudioDevicePropertySafetyOffset};
use objc2_core_audio_types::{AudioBufferList, AudioStreamBasicDescription, AudioTimeStamp};

use crate::error::{Error, Result};
use crate::ffi::audio_output::{PriscAudioOutput, PriscAudioOutputInfo};
use crate::ffi::log::{self, PriscInteropLogLevel};
use crate::panic::{self, CallbackPanic};
use crate::platform::macos::devices::{coreaudio_error, float32_interleaved};

/// The host's pull, as the export receives it.
pub(crate) type RenderFn = unsafe extern "C" fn(user: *mut c_void, samples: *mut f32, frames: u32, channels: u32);

/// An open default-output unit: instantiated, formatted, callback-installed and initialised. The
/// box's address is the refcon CoreAudio hands [`render_trampoline`], so the trampoline finds it
/// with one cast.
pub(crate) struct Output {
    /// Owned; disposed by [`Output::destroy`] (or the failure path of create), nowhere else.
    unit: AudioComponentInstance,
    /// Written before `AudioUnitInitialize` and never changed, so the IO thread reads them freely.
    render: RenderFn,
    user: *mut c_void,
    channels: u32,
    /// Control path only (start / stop / destroy, which the caller serialises).
    running: AtomicBool,
    /// Where the trampoline records a caught panic; the handle's next export reports it.
    panic: CallbackPanic,
    /// Test seam: makes this output's next render panic after the buffer was silenced, so the
    /// tests can show a panicking callback yields silence and no abort. Per output, so another
    /// test's unit, pulled by its own IO thread, cannot consume it.
    #[cfg(test)]
    panic_next: AtomicBool,
}

// The unit and `user` are raw pointers. The IO-thread fields are immutable after create, `running`
// is atomic, the control path is serialised by the caller, and destroy uninitialises the unit (which
// waits out the IO proc) before anything is dropped.
unsafe impl Send for Output {}
unsafe impl Sync for Output {}

/// CoreAudio's IO thread. `extern "C-unwind"` because that is the generated `AURenderCallback`
/// type; never-unwind is restored by `callback_guard`. Always answers noErr.
unsafe extern "C-unwind" fn render_trampoline(
    ref_con: NonNull<c_void>,
    _flags: NonNull<AudioUnitRenderActionFlags>,
    _time: NonNull<AudioTimeStamp>,
    _bus: u32,
    _frames: u32,
    io_data: *mut AudioBufferList,
) -> i32 {
    panic::callback_guard(&ref_con.cast::<Output>().as_ref().panic, 0, || {
        ref_con
            .cast::<Output>()
            .as_ref()
            .render(io_data);
        0
    })
}

impl Output {
    /// The IO-thread body: silence the whole writable region first (so a panic anywhere after this
    /// leaves silence, never last cycle's audio), then hand the host `(samples, frames, channels)`.
    /// The byte count, not the frame count CoreAudio passes, bounds the region; for the packed
    /// interleaved format create sets the two agree.
    unsafe fn render(&self, io_data: *mut AudioBufferList) {
        let Some(list) = io_data.as_ref() else {
            return;
        };
        if list.mNumberBuffers == 0 || list.mBuffers[0].mData.is_null() {
            return;
        }
        let buffer = &list.mBuffers[0];
        let samples = buffer.mData.cast::<f32>();
        let floats = buffer.mDataByteSize as usize / mem::size_of::<f32>();
        ptr::write_bytes(samples, 0, floats);
        #[cfg(test)]
        if self.panic_next.swap(false, Ordering::AcqRel) {
            panic!("render panic injected by a test");
        }
        let frames = floats / self.channels as usize;
        if frames > 0 {
            (self.render)(self.user, samples, frames as u32, self.channels);
        }
    }

    /// Opens the unit. Every failure after the instance exists disposes it, so no half-built unit
    /// escapes; it does not start the unit.
    pub(crate) fn create(
        sample_rate_hz: u32,
        channels: u32,
        render: RenderFn,
        user: *mut c_void,
    ) -> Result<(Box<Output>, PriscAudioOutputInfo)> {
        let mut description = AudioComponentDescription {
            componentType: kAudioUnitType_Output,
            componentSubType: kAudioUnitSubType_DefaultOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0,
            componentFlagsMask: 0,
        };
        // SAFETY: a lookup with a description on the stack.
        let component = unsafe { AudioComponentFindNext(ptr::null_mut(), NonNull::from(&mut description)) };
        if component.is_null() {
            return Err(Error::capability("No default CoreAudio output unit is available."));
        }
        let mut unit: AudioComponentInstance = ptr::null_mut();
        // SAFETY: the out pointer is a local.
        let status = unsafe { AudioComponentInstanceNew(component, NonNull::from(&mut unit)) };
        if status != 0 {
            return Err(coreaudio_error(status, "creating the output unit"));
        }
        // Boxed before the callback is installed: the box's address is the refcon.
        let output = Box::new(Output {
            unit,
            render,
            user,
            channels,
            running: AtomicBool::new(false),
            panic: CallbackPanic::new(),
            #[cfg(test)]
            panic_next: AtomicBool::new(false),
        });
        match output.configure(sample_rate_hz) {
            Ok(info) => Ok((output, info)),
            Err(e) => {
                // Never initialised, so no AudioUnitUninitialize.
                // SAFETY: the instance is ours and nothing else holds it.
                unsafe { AudioComponentInstanceDispose(output.unit) };
                Err(e)
            }
        }
    }

    /// Interleaved packed float32 on the input scope; the unit resamples to the device's rate
    /// itself. Then the callback, the initialisation, and the latency the device now reports.
    fn configure(&self, sample_rate_hz: u32) -> Result<PriscAudioOutputInfo> {
        let format = float32_interleaved(sample_rate_hz, self.channels);
        // SAFETY: the property data is the struct on the stack, with its size.
        let status = unsafe {
            AudioUnitSetProperty(
                self.unit,
                kAudioUnitProperty_StreamFormat,
                kAudioUnitScope_Input,
                0,
                ptr::addr_of!(format).cast(),
                mem::size_of::<AudioStreamBasicDescription>() as u32,
            )
        };
        if status != 0 {
            return Err(coreaudio_error(status, "setting the output stream format"));
        }
        let callback = AURenderCallbackStruct {
            inputProc: Some(render_trampoline),
            inputProcRefCon: ptr::from_ref(self).cast_mut().cast(),
        };
        // SAFETY: as above; the refcon is this boxed Output, which outlives the unit.
        let status = unsafe {
            AudioUnitSetProperty(
                self.unit,
                kAudioUnitProperty_SetRenderCallback,
                kAudioUnitScope_Input,
                0,
                ptr::addr_of!(callback).cast(),
                mem::size_of::<AURenderCallbackStruct>() as u32,
            )
        };
        if status != 0 {
            return Err(coreaudio_error(status, "installing the render callback"));
        }
        // SAFETY: a formatted, callback-installed unit.
        let status = unsafe { AudioUnitInitialize(self.unit) };
        if status != 0 {
            return Err(coreaudio_error(status, "initializing the output unit"));
        }
        Ok(PriscAudioOutputInfo {
            latency_frames: self.latency_frames(),
            sample_rate_hz: self
                .device_sample_rate()
                .unwrap_or(sample_rate_hz),
        })
    }

    /// The device's IO buffer plus its latency plus its safety offset, in device frames; 0 when it
    /// publishes none of the three. Each is optional: a failed read means "not published".
    fn latency_frames(&self) -> u32 {
        let mut total: u32 = 0;
        for property in [
            kAudioDevicePropertyBufferFrameSize,
            kAudioDevicePropertyLatency,
            kAudioDevicePropertySafetyOffset,
        ] {
            let mut frames: u32 = 0;
            let mut size = mem::size_of::<u32>() as u32;
            // SAFETY: one u32 out value with its size.
            let status = unsafe {
                AudioUnitGetProperty(
                    self.unit,
                    property,
                    kAudioUnitScope_Output,
                    0,
                    NonNull::from(&mut frames).cast(),
                    NonNull::from(&mut size),
                )
            };
            if status == 0 {
                total = total.saturating_add(frames);
            }
        }
        total
    }

    /// The device side of the unit (output scope of element 0), whose rate the latency frames count
    /// in; `None` when the unit will not say.
    fn device_sample_rate(&self) -> Option<u32> {
        // SAFETY: an all-zero AudioStreamBasicDescription is a valid value of the plain struct.
        let mut format: AudioStreamBasicDescription = unsafe { mem::zeroed() };
        let mut size = mem::size_of::<AudioStreamBasicDescription>() as u32;
        // SAFETY: the out struct and its size.
        let status = unsafe {
            AudioUnitGetProperty(
                self.unit,
                kAudioUnitProperty_StreamFormat,
                kAudioUnitScope_Output,
                0,
                NonNull::from(&mut format).cast(),
                NonNull::from(&mut size),
            )
        };
        (status == 0 && format.mSampleRate >= 1.0 && format.mSampleRate < f64::from(u32::MAX)).then(|| format.mSampleRate.round() as u32)
    }

    /// The output behind a live handle; `PRISC_E_INVALID_ARG` for null, `PRISC_E_PANIC` when the
    /// trampoline caught a panic since the last call.
    ///
    /// # Safety
    /// `handle` is null or came from `prisc_audio_output_create` and was not destroyed.
    pub(crate) unsafe fn live<'a>(handle: *mut PriscAudioOutput) -> Result<&'a Output> {
        let output = handle
            .cast::<Output>()
            .as_ref()
            .ok_or_else(|| Error::null("output"))?;
        output.panic.check()?;
        Ok(output)
    }

    pub(crate) fn start(&self) -> Result<()> {
        if self.running.load(Ordering::Acquire) {
            return Ok(());
        }
        // SAFETY: an initialised unit this handle owns.
        let status = unsafe { AudioOutputUnitStart(self.unit) };
        if status != 0 {
            return Err(coreaudio_error(status, "starting the output unit"));
        }
        self.running.store(true, Ordering::Release);
        Ok(())
    }

    pub(crate) fn stop(&self) -> Result<()> {
        if !self.running.load(Ordering::Acquire) {
            return Ok(());
        }
        // SAFETY: as in start.
        let status = unsafe { AudioOutputUnitStop(self.unit) };
        if status != 0 {
            return Err(coreaudio_error(status, "stopping the output unit"));
        }
        self.running.store(false, Ordering::Release);
        Ok(())
    }

    /// Stop, uninitialise (which waits out an IO proc already running), dispose, and only then
    /// drop the box holding the callback and `user`. All three run whatever each answers; a failure
    /// is diagnostic only and goes to the log, as does a trampoline panic nobody reported yet.
    pub(crate) fn destroy(output: Box<Output>) {
        // SAFETY: the unit is ours; after these three it no longer exists.
        let statuses = unsafe {
            [
                AudioOutputUnitStop(output.unit),
                AudioUnitUninitialize(output.unit),
                AudioComponentInstanceDispose(output.unit),
            ]
        };
        if let Some(status) = statuses.into_iter().find(|&s| s != 0) {
            log::emit(
                PriscInteropLogLevel::Warn,
                &coreaudio_error(status, "destroying the output unit").message,
            );
        }
        if let Some(text) = output.panic.take() {
            log::emit(PriscInteropLogLevel::Error, &format!("the audio output render callback: {text}"));
        }
        drop(output);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicU64;
    use std::thread::sleep;
    use std::time::{Duration, Instant};

    /// A test's own pull counter, reached through `user`; leaked because the destroy test reads it
    /// after the handle is gone, and per test because cargo runs the tests in parallel.
    fn counter() -> &'static AtomicU64 {
        Box::leak(Box::new(AtomicU64::new(0)))
    }

    unsafe extern "C" fn count(user: *mut c_void, samples: *mut f32, frames: u32, channels: u32) {
        let n = frames as usize * channels as usize;
        // A non-zero fill, so a silenced buffer is distinguishable from a rendered one.
        for i in 0..n {
            *samples.add(i) = 0.25;
        }
        (*user.cast::<AtomicU64>()).fetch_add(n as u64, Ordering::Relaxed);
    }

    /// An open unit at 48 kHz stereo, or `None` on a machine with no usable output (a build agent
    /// can find the software unit and still fail at initialise, so any failure skips).
    fn open(pulled: &'static AtomicU64) -> Option<Box<Output>> {
        match Output::create(48_000, 2, count, ptr::from_ref(pulled).cast_mut().cast()) {
            Ok((output, info)) => {
                assert!(info.sample_rate_hz > 0);
                Some(output)
            }
            Err(e) => {
                eprintln!("skipped: no usable CoreAudio output ({:?}: {})", e.status, e.message);
                None
            }
        }
    }

    fn wait_for(pulled: &AtomicU64, at_least: u64) -> bool {
        let deadline = Instant::now() + Duration::from_secs(3);
        while Instant::now() < deadline {
            if pulled.load(Ordering::Relaxed) >= at_least {
                return true;
            }
            // A coarse latch on a test thread, not a precise wait.
            sleep(Duration::from_millis(10));
        }
        false
    }

    #[test]
    fn the_device_pulls_after_start_and_stops_on_destroy() {
        let pulled = counter();
        let Some(output) = open(pulled) else { return };
        assert_eq!(pulled.load(Ordering::Relaxed), 0, "pulled before start");
        if let Err(e) = output.start() {
            eprintln!("skipped: the unit would not start ({})", e.message);
            Output::destroy(output);
            return;
        }
        assert!(wait_for(pulled, 1), "the device never pulled after start");
        output
            .start()
            .expect("a second start is a no-op");
        output.stop().expect("stop");
        sleep(Duration::from_millis(200));
        let latched = pulled.load(Ordering::Relaxed);
        sleep(Duration::from_millis(300));
        assert_eq!(pulled.load(Ordering::Relaxed), latched, "pulled after stop");
        output.start().expect("restart");
        assert!(wait_for(pulled, latched + 1), "no pull after restart");
        Output::destroy(output);
        sleep(Duration::from_millis(200));
        let latched = pulled.load(Ordering::Relaxed);
        sleep(Duration::from_millis(300));
        assert_eq!(pulled.load(Ordering::Relaxed), latched, "the callback outlived destroy");
    }

    #[test]
    fn destroy_without_start_tears_down() {
        let Some(output) = open(counter()) else { return };
        Output::destroy(output);
    }

    /// A panic on the IO thread is caught: the buffer stays silent, nothing aborts, the pull goes
    /// on, and the next export on the handle reports the panic once.
    #[test]
    fn a_panicking_render_yields_silence_and_is_reported_once() {
        let pulled = counter();
        let Some(output) = open(pulled) else { return };
        let mut storage = vec![1.0f32; 64];
        let mut list = AudioBufferList {
            mNumberBuffers: 1,
            mBuffers: [objc2_core_audio_types::AudioBuffer {
                mNumberChannels: 2,
                mDataByteSize: (storage.len() * mem::size_of::<f32>()) as u32,
                mData: storage.as_mut_ptr().cast(),
            }],
        };
        let refcon = NonNull::from(&*output).cast::<c_void>();
        let mut flags = AudioUnitRenderActionFlags(0);
        // SAFETY: an all-zero AudioTimeStamp is a valid value of the plain struct.
        let mut time: AudioTimeStamp = unsafe { mem::zeroed() };
        output
            .panic_next
            .store(true, Ordering::Release);
        let status = unsafe { render_trampoline(refcon, NonNull::from(&mut flags), NonNull::from(&mut time), 0, 32, &mut list) };
        assert_eq!(status, 0);
        assert!(storage.iter().all(|&s| s == 0.0), "a caught panic must leave silence");
        assert_eq!(pulled.load(Ordering::Relaxed), 0, "the host callback must not run after the panic");

        let handle = ptr::from_ref(&*output)
            .cast_mut()
            .cast::<PriscAudioOutput>();
        let e = unsafe { Output::live(handle) }
            .err()
            .expect("the panic is reported");
        assert_eq!(e.status, crate::ffi::status::PriscInteropStatus::Panic);
        assert!(e.message.contains("injected"), "{}", e.message);
        assert!(unsafe { Output::live(handle) }.is_ok(), "reported once");

        // And the normal path renders again.
        let status = unsafe { render_trampoline(refcon, NonNull::from(&mut flags), NonNull::from(&mut time), 0, 32, &mut list) };
        assert_eq!(status, 0);
        assert!(storage.iter().all(|&s| s == 0.25));
        Output::destroy(output);
    }
}
