//! Synthetic frames through the readback ring and the NV12 converter, end to end through the
//! exports, without Skia (Windows; WARP on CI with `PRISC_GPU_ALLOW_WARP=1`).
//!
//! The shape is the render's: several slots in flight, the drawing on one thread and the fence
//! waits on another, enough frames that every slot is reused. What stands in for Skia is a
//! render-target view over each slot texture and one `ClearRenderTargetView` on the device's own
//! direct queue. Skips with a printed reason on a machine with no Direct3D 12 device at all.

#[cfg(windows)]
mod harness {
    use std::ffi::c_void;
    use std::ptr;
    use std::sync::mpsc;
    use std::thread;

    use prisc_interop::ffi::error::prisc_last_error_message;
    use prisc_interop::ffi::gpu::*;
    use prisc_interop::ffi::status::PriscInteropStatus as S;
    use prisc_interop::ffi::types::PriscString;
    use windows::Win32::Graphics::Direct3D12::{
        ID3D12CommandAllocator, ID3D12CommandList, ID3D12CommandQueue, ID3D12DescriptorHeap, ID3D12Device, ID3D12GraphicsCommandList,
        ID3D12Resource, D3D12_COMMAND_LIST_TYPE_DIRECT, D3D12_CPU_DESCRIPTOR_HANDLE, D3D12_DESCRIPTOR_HEAP_DESC,
        D3D12_DESCRIPTOR_HEAP_FLAG_NONE, D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
    };
    use windows_core::Interface;

    /// Four slots by twelve frames: every slot is begun, submitted and waited three times.
    const SLOTS: u32 = 4;
    const FRAMES: u32 = 12;
    const WIDTH: u32 = 64;
    const HEIGHT: u32 = 48;
    const WAIT_MS: u32 = 30_000;

    /// A ring handle on its way to the waiting thread; the waits are documented free-threaded.
    struct RingPtr(*mut PriscGpuReadbackRing);
    // SAFETY: the exports this pointer reaches are callable from any thread, and the main thread
    // outlives the worker, so the handle is live for every call the worker makes.
    unsafe impl Send for RingPtr {}

    struct Frame {
        slot: u32,
        colour: [u8; 4],
    }

    /// Frame `n`'s colour as BGRA memory bytes; multiples of 17 survive the float clear exactly.
    fn colour_of(frame: u32) -> [u8; 4] {
        let r = (17 * (frame % 15) + 3) as u8;
        let g = (17 * ((frame + 5) % 15) + 1) as u8;
        let b = (17 * ((frame + 9) % 15) + 2) as u8;
        [b, g, r, 0xFF]
    }

    fn clear_value(colour: [u8; 4]) -> [f32; 4] {
        [
            f32::from(colour[2]) / 255.0,
            f32::from(colour[1]) / 255.0,
            f32::from(colour[0]) / 255.0,
            1.0,
        ]
    }

    fn last_error() -> String {
        let len = unsafe { prisc_last_error_message(ptr::null_mut(), 0) };
        let mut buf = vec![0u8; len];
        unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) };
        String::from_utf8_lossy(&buf).into_owned()
    }

    /// One owned reference on an interface an export lent as a raw pointer.
    unsafe fn own<T: Interface>(raw: *mut c_void) -> T {
        unsafe { T::from_raw_borrowed(&raw) }
            .expect("a live handle lends a non-null pointer")
            .clone()
    }

    fn create_device(test: &str) -> Option<(*mut PriscGpuDevice, PriscGpuDeviceInfo)> {
        let flags = if std::env::var("PRISC_GPU_ALLOW_WARP").as_deref() == Ok("1") {
            PriscGpuDeviceFlags::AllowSoftwareAdapter as u32
        } else {
            0
        };
        let mut device: *mut PriscGpuDevice = ptr::null_mut();
        let mut info = PriscGpuDeviceInfo {
            backend: 0,
            _reserved: 0,
            adapter_name: PriscString {
                ptr: ptr::null_mut(),
                len: 0,
            },
            d3d12_adapter: ptr::null_mut(),
            d3d12_device: ptr::null_mut(),
            d3d12_queue: ptr::null_mut(),
            metal_device: ptr::null_mut(),
            metal_queue: ptr::null_mut(),
        };
        let status = unsafe { prisc_gpu_device_create(flags, &mut device, &mut info) };
        if status == S::Capability as i32 {
            println!("skipping {test}: no Direct3D 12 device ({})", last_error());
            return None;
        }
        assert_eq!(status, S::Ok as i32, "{test}: prisc_gpu_device_create: {}", last_error());
        Some((device, info))
    }

    /// The Skia stand-in: an RTV per slot and one direct-queue command list per slot.
    struct Painter {
        queue: ID3D12CommandQueue,
        heap: ID3D12DescriptorHeap,
        stride: u32,
        allocators: Vec<ID3D12CommandAllocator>,
        lists: Vec<ID3D12GraphicsCommandList>,
    }

    impl Painter {
        fn new(device: &ID3D12Device, queue: ID3D12CommandQueue, textures: &[*mut c_void]) -> Painter {
            let desc = D3D12_DESCRIPTOR_HEAP_DESC {
                Type: D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
                NumDescriptors: textures.len() as u32,
                Flags: D3D12_DESCRIPTOR_HEAP_FLAG_NONE,
                NodeMask: 0,
            };
            let heap: ID3D12DescriptorHeap = unsafe { device.CreateDescriptorHeap(&desc) }.expect("an RTV descriptor heap");
            let stride = unsafe { device.GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV) };
            let start = unsafe { heap.GetCPUDescriptorHandleForHeapStart() };
            let mut allocators = Vec::new();
            let mut lists = Vec::new();
            for (index, texture) in textures.iter().enumerate() {
                let resource: ID3D12Resource = unsafe { own(*texture) };
                let handle = D3D12_CPU_DESCRIPTOR_HANDLE {
                    ptr: start.ptr + index * stride as usize,
                };
                unsafe { device.CreateRenderTargetView(&resource, None, handle) };
                let allocator: ID3D12CommandAllocator =
                    unsafe { device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT) }.expect("a direct command allocator");
                let list: ID3D12GraphicsCommandList =
                    unsafe { device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None) }
                        .expect("a direct command list");
                unsafe { list.Close() }.expect("a closed command list");
                allocators.push(allocator);
                lists.push(list);
            }
            Painter {
                queue,
                heap,
                stride,
                allocators,
                lists,
            }
        }

        /// Clears one slot on the direct queue. Resetting its allocator is safe: the ring only
        /// hands a slot back once its previous frame has been waited for.
        fn clear(&self, slot: u32, colour: [u8; 4]) {
            let allocator = &self.allocators[slot as usize];
            let list = &self.lists[slot as usize];
            unsafe {
                allocator.Reset().expect("a reset allocator");
                list.Reset(allocator, None)
                    .expect("a reset command list");
                let start = self
                    .heap
                    .GetCPUDescriptorHandleForHeapStart();
                let handle = D3D12_CPU_DESCRIPTOR_HANDLE {
                    ptr: start.ptr + (slot * self.stride) as usize,
                };
                list.ClearRenderTargetView(handle, &clear_value(colour), None);
                list.Close().expect("a closed command list");
                self.queue.ExecuteCommandLists(&[Some(
                    list.cast::<ID3D12CommandList>()
                        .expect("a command list"),
                )]);
            }
        }
    }

    /// Every pixel of every row, honouring the pitch.
    fn check(address: *const u8, row_bytes: u32, colour: [u8; 4]) -> Result<(), String> {
        for row in 0..HEIGHT {
            let line = unsafe { std::slice::from_raw_parts(address.add(row as usize * row_bytes as usize), WIDTH as usize * 4) };
            for column in 0..WIDTH as usize {
                let pixel = &line[column * 4..column * 4 + 4];
                if pixel != colour {
                    return Err(format!("row {row} column {column} is {pixel:02X?}, expected {colour:02X?}"));
                }
            }
        }
        Ok(())
    }

    #[test]
    fn synthetic_frames_through_the_ring_without_skia() {
        let Some((device, device_info)) = create_device("synthetic_frames_through_the_ring_without_skia") else {
            return;
        };

        // Only a missing device is a skip: a device that cannot build a ring is a failure.
        let mut ring: *mut PriscGpuReadbackRing = ptr::null_mut();
        let mut info = PriscGpuRingInfo::default();
        let status = unsafe { prisc_gpu_readback_ring_create_windows(device, WIDTH, HEIGHT, SLOTS, &mut ring, &mut info) };
        assert_eq!(status, S::Ok as i32, "prisc_gpu_readback_ring_create_windows: {}", last_error());
        assert_eq!((info.width, info.height, info.slots), (WIDTH, HEIGHT, SLOTS));
        assert!(info.row_pitch >= WIDTH * 4 && info.row_pitch % 256 == 0, "{}", info.row_pitch);

        let mut textures = Vec::new();
        for slot in 0..SLOTS {
            let mut texture: *mut c_void = ptr::null_mut();
            assert_eq!(
                unsafe { prisc_gpu_readback_ring_begin_windows(ring, slot, &mut texture) },
                S::Ok as i32
            );
            assert!(!texture.is_null());
            assert_eq!(unsafe { prisc_gpu_readback_ring_cancel_begin_windows(ring, slot) }, S::Ok as i32);
            textures.push(texture);
        }
        let painter = Painter::new(
            &unsafe { own::<ID3D12Device>(device_info.d3d12_device) },
            unsafe { own(device_info.d3d12_queue) },
            &textures,
        );

        // The waits run on their own thread: the composer must never block on a fence.
        let (work_tx, work_rx) = mpsc::channel::<Frame>();
        let (done_tx, done_rx) = mpsc::channel::<Result<(), String>>();
        let waiting = RingPtr(ring);
        let waiter = thread::spawn(move || {
            let ring = waiting;
            for frame in work_rx {
                let mut address: *const u8 = ptr::null();
                let mut row_bytes = 0u32;
                let status = unsafe { prisc_gpu_readback_ring_wait_windows(ring.0, frame.slot, WAIT_MS, &mut address, &mut row_bytes) };
                let outcome = if status != S::Ok as i32 {
                    Err(format!("prisc_gpu_readback_ring_wait_windows returned {status}"))
                } else {
                    check(address, row_bytes, frame.colour)
                };
                done_tx
                    .send(outcome)
                    .expect("the main thread is listening");
            }
        });

        let mut in_flight = 0u32;
        for frame in 0..FRAMES {
            if in_flight == SLOTS {
                done_rx
                    .recv()
                    .expect("the waiter is alive")
                    .expect("a frame read back what was drawn");
                in_flight -= 1;
            }
            let slot = frame % SLOTS;
            let colour = colour_of(frame);
            let mut texture: *mut c_void = ptr::null_mut();
            assert_eq!(
                unsafe { prisc_gpu_readback_ring_begin_windows(ring, slot, &mut texture) },
                S::Ok as i32
            );
            assert_eq!(texture, textures[slot as usize], "a slot's texture is the same every frame");
            painter.clear(slot, colour);
            let mut value = 0u64;
            assert_eq!(
                unsafe { prisc_gpu_readback_ring_submit_windows(ring, slot, &mut value) },
                S::Ok as i32
            );
            // The creation self-check spent value 1.
            assert_eq!(value, u64::from(frame) + 2, "fence values are one monotonic sequence");
            work_tx
                .send(Frame {
                    slot,
                    colour,
                })
                .expect("the waiter is alive");
            in_flight += 1;
        }
        drop(work_tx);
        while in_flight > 0 {
            done_rx
                .recv()
                .expect("the waiter is alive")
                .expect("a frame read back what was drawn");
            in_flight -= 1;
        }
        waiter
            .join()
            .expect("the waiting thread finished");

        assert_eq!(unsafe { prisc_gpu_readback_ring_destroy_windows(ring) }, S::Ok as i32);
        assert_eq!(unsafe { prisc_gpu_device_destroy(device) }, S::Ok as i32);
    }

    /// Critique F9 through the exports: FFmpeg's free path releases a texture after the converter,
    /// the ring and the device handles are all gone.
    #[test]
    fn nv12_texture_release_after_every_handle_was_destroyed() {
        let Some((device, _)) = create_device("nv12_texture_release_after_every_handle_was_destroyed") else {
            return;
        };
        let mut ring: *mut PriscGpuReadbackRing = ptr::null_mut();
        let mut info = PriscGpuRingInfo::default();
        assert_eq!(
            unsafe { prisc_gpu_readback_ring_create_windows(device, 32, 32, 2, &mut ring, &mut info) },
            S::Ok as i32,
            "{}",
            last_error()
        );
        let mut converter: *mut PriscGpuNv12Converter = ptr::null_mut();
        let status = unsafe { prisc_gpu_nv12_converter_create_windows(device, ring, &mut converter) };
        if status == S::Capability as i32 {
            println!("skipping: this GPU cannot run the NV12 converter ({})", last_error());
            unsafe {
                prisc_gpu_readback_ring_destroy_windows(ring);
                prisc_gpu_device_destroy(device);
            }
            return;
        }
        assert_eq!(status, S::Ok as i32, "{}", last_error());
        assert_eq!(
            unsafe { prisc_gpu_nv12_converter_self_check_windows(converter, WAIT_MS) },
            S::Ok as i32,
            "{}",
            last_error()
        );

        let mut texture: *mut c_void = ptr::null_mut();
        assert_eq!(
            unsafe { prisc_gpu_nv12_texture_acquire_windows(converter, &mut texture) },
            S::Ok as i32
        );
        assert!(!texture.is_null());
        unsafe {
            assert_eq!(prisc_gpu_nv12_converter_drain_windows(converter, WAIT_MS), S::Ok as i32);
            assert_eq!(prisc_gpu_nv12_converter_destroy_windows(converter), S::Ok as i32);
            assert_eq!(prisc_gpu_readback_ring_destroy_windows(ring), S::Ok as i32);
            assert_eq!(prisc_gpu_device_destroy(device), S::Ok as i32);
            assert_eq!(prisc_gpu_nv12_texture_release_windows(texture), S::Ok as i32);
            assert_eq!(prisc_gpu_nv12_texture_release_windows(texture), S::None as i32);
        }
    }
}

/// The ring and the converter are Direct3D concepts; off Windows this file has nothing to drive.
#[cfg(not(windows))]
#[test]
fn the_ring_harness_runs_on_windows_only() {
    println!("skipping: the readback ring and the NV12 converter are implemented on Windows only");
}
