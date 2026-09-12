using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Clowd.VideoSDK.Media;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The zero-copy encode path on Windows: hands the <see cref="D3D12ReadbackRing"/>'s render
    /// targets to a Direct3D 11 device on the same adapter and turns each composed frame into an
    /// NV12 texture NVENC or AMF read directly (<see cref="HardwareFrames"/>), so the picture
    /// never leaves video memory — no readback DMA, no swscale, no encoder-side upload.
    ///
    /// <para><b>Mechanism.</b> A D3D11 device is created on the ring's adapter (checked by LUID:
    /// shared resources are only valid on one GPU). Each slot texture is opened through an NT
    /// handle (<c>ID3D12Device::CreateSharedHandle</c> → <c>ID3D11Device1::OpenSharedResource1</c>;
    /// sharing needs the ring's <c>HEAP_FLAG_SHARED</c> + <c>ALLOW_SIMULTANEOUS_ACCESS</c>
    /// textures), and so are the ring's two fences (<c>ID3D11Device5::OpenSharedFence</c>).
    /// Per frame, on the pipeline's convert thread: <c>ID3D11DeviceContext4::Wait</c> on the
    /// render fence at the value the ring's <see cref="D3D12ReadbackRing.SubmitShared"/> returned
    /// (a GPU-side wait: the composer thread never blocks), a video-processor blit
    /// (<c>ID3D11VideoContext::VideoProcessorBlt</c>) from the shared BGRA texture into a pooled
    /// NV12 texture, <c>Signal</c> on the consumed fence with the same value (which is what
    /// <see cref="D3D12ReadbackRing.ReleaseShared"/> waits for before Skia may draw into the slot
    /// again), and a flush. The NV12 texture goes to the encoder as a
    /// <see cref="HardwareFrame"/>; it returns to the pool when the encoder drops its reference —
    /// the only correct "done reading" signal, see <see cref="EncoderTexturePool"/>. So the ring
    /// keeps its four slots and each is free again as soon as its blit has run, while the
    /// encoder's in-flight depth (NVENC maps ~15 inputs at these settings) is absorbed by the
    /// NV12 pool, whose textures are a third the size of the BGRA render targets.</para>
    ///
    /// <para><b>Why a GPU colour conversion rather than BGRA frames.</b> NVENC does accept BGRA
    /// D3D11 textures (<c>NV_ENC_BUFFER_FORMAT_ARGB</c>; verified), but converts them in the
    /// driver with a fixed BT.601-limited matrix and tags the stream BT.470BG — and AMF's FFmpeg
    /// 7.1 encoder takes no RGB D3D11 format at all. The video processor is run with an explicit
    /// BT.601 matrix, full-range RGB in, 16–235 out and driver auto-processing off: the same
    /// matrix swscale applies on the readback path, so the zero-copy output matches the fallback
    /// it may silently be replaced by, every encoder sees NV12 like the system-memory path, and
    /// streams stay untagged as before. A one-frame self-check at creation converts a known
    /// colour and reads the NV12 back through a staging texture, so a driver that ignores the
    /// colour-space flags (or a broken share) degrades to readback at startup rather than
    /// producing a wrong render.</para>
    ///
    /// <para>Lifetime: create on the ring's (composer) thread — the self-check draws through the
    /// ring — call <see cref="Convert"/> from one thread at a time (the convert stage), and
    /// dispose after the encoder that was opened over <see cref="Frames"/> has been closed and
    /// before the ring. Creation is all-or-nothing: any refusal (no D3D11.4, an adapter mismatch,
    /// a share that will not open, an unsupported video-processor format, a self-check
    /// mismatch) reports one reason and leaves nothing behind — the ring's slot 0, which the
    /// self-check draws through, is idle again — so the caller stays on readback over the same
    /// ring.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class D3D11EncodeBridge : IEncodeBridge
    {
        /// <summary>How far a decoded self-check sample may sit from the BT.601-limited value of
        /// the probe colour: rounding and the video processor's own arithmetic account for a
        /// level or two; the wrong matrix (BT.709: Y off by 9 for the probe colour) or range
        /// (full: V off by 8) lands well outside.</summary>
        private const int SelfCheckTolerance = 4;

        private readonly D3D12ReadbackRing _ring;
        private readonly IntPtr[] _sharedTextures;
        private readonly IntPtr[] _inputViews;
        private readonly Dictionary<IntPtr, IntPtr> _outputViews = new Dictionary<IntPtr, IntPtr>(); // pool texture → ID3D11VideoProcessorOutputView
        private readonly object _viewSync = new object();
        private IntPtr _device, _context, _device1, _device5, _context4, _videoDevice, _videoContext;
        private IntPtr _enumerator, _processor, _renderFence, _consumedFence;
        private HardwareFrames _frames;
        private ulong _lastSignaled;
        private bool _disposed;

        private D3D11EncodeBridge(D3D12ReadbackRing ring)
        {
            _ring = ring;
            _sharedTextures = new IntPtr[ring.SlotCount];
            _inputViews = new IntPtr[ring.SlotCount];
        }

        /// <summary>The encoder-facing side: what <see cref="Mp4WriterOptions.HardwareFrames"/> takes.</summary>
        public HardwareFrames Frames => _frames;

        public int Width => _ring.Width;

        public int Height => _ring.Height;

        /// <summary>For the diagnostic line that names the encode path.</summary>
        public string Description =>
            $"Direct3D 11 zero-copy: {_ring.SlotCount} shared render targets, VideoProcessor BGRA->NV12 (BT.601 limited) into pooled encoder textures";

        /// <summary>The ring's <see cref="D3D12ReadbackRing.SubmitShared"/> for the slot.</summary>
        public ulong SubmitFrame(int slot) => _ring.SubmitShared(slot);

        /// <summary>The ring's <see cref="D3D12ReadbackRing.ReleaseShared"/> for the slot: waits for
        /// the consumed-fence signal <see cref="Convert"/> queued behind the slot's blit.</summary>
        public void ReleaseSlot(int slot) => _ring.ReleaseShared(slot);

        /// <summary>
        /// Builds the bridge over a shareable ring, or returns null with the reason. Must run on
        /// the ring's thread (the self-check composes a frame through it, using slot 0, which
        /// must be idle and is idle again on return either way).
        /// </summary>
        /// <param name="beforeSelfCheck">For tests: runs once the device, the shares and the
        /// frame pool are up, just before the self-check frame goes through. Throwing from it,
        /// or breaking something it is handed, stands in for a driver that refuses the first
        /// conversion.</param>
        public static D3D11EncodeBridge TryCreate(D3D12ReadbackRing ring, out string failureReason,
            Action<D3D11EncodeBridge> beforeSelfCheck = null)
        {
            ArgumentNullException.ThrowIfNull(ring);
            if (!ring.Shareable)
            {
                failureReason = "the readback ring's textures and fences are not shareable on this driver";
                return null;
            }

            var bridge = new D3D11EncodeBridge(ring);
            try
            {
                if (!bridge.Initialize(beforeSelfCheck, out failureReason))
                    return null;
                var built = bridge;
                bridge = null; // ownership passes to the caller
                return built;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                       or DllNotFoundException or EntryPointNotFoundException)
            {
                // a COM call refused mid-way, the self-check's fence never signalling, or no
                // d3d11.dll at all — every one of them means "readback on this machine"
                failureReason = ex.Message;
                return null;
            }
            finally
            {
                bridge?.Dispose();
            }
        }

        private bool Initialize(Action<D3D11EncodeBridge> beforeSelfCheck, out string failureReason)
        {
            int w = _ring.Width, h = _ring.Height;

            // The video processor needs the video-support flag on some runtimes; a driver that
            // refuses that flag (pre-WDDM 1.2) gets a plain device, and the enumerator below
            // decides whether the processor exists.
            int hr = D3D11Backend.CreateDevice(_ring.Adapter,
                D3D11Backend.CreateDeviceBgraSupport | D3D11Backend.CreateDeviceVideoSupport,
                out _device, out _context, out int featureLevel);
            if (hr < 0)
                hr = D3D11Backend.CreateDevice(_ring.Adapter, D3D11Backend.CreateDeviceBgraSupport,
                    out _device, out _context, out featureLevel);
            if (hr < 0)
                return Fail(out failureReason, "D3D11CreateDevice", hr);

            // Same GPU or nothing: a shared handle opened on another adapter is undefined, and
            // the render fence would never be seen by the other device's timeline.
            hr = D3D11Backend.GetAdapterLuid(_device, out long luid11);
            if (hr < 0)
                return Fail(out failureReason, "IDXGIAdapter::GetDesc", hr);
            long luid12 = D3D12Backend.GetAdapterLuid(_ring.Device);
            if (luid11 != luid12)
            {
                failureReason = $"the Direct3D 11 device landed on adapter LUID 0x{luid11:X} but the composer's is 0x{luid12:X}";
                return false;
            }

            hr = D3D12Backend.QueryInterface(_device, D3D11Backend.IID_ID3D11Device1, out _device1);
            if (hr < 0)
                return Fail(out failureReason, "QueryInterface(ID3D11Device1)", hr);
            hr = D3D12Backend.QueryInterface(_device, D3D11Backend.IID_ID3D11Device5, out _device5);
            if (hr < 0)
                return Fail(out failureReason, "QueryInterface(ID3D11Device5 — needs the Direct3D 11.4 runtime)", hr);
            hr = D3D12Backend.QueryInterface(_context, D3D11Backend.IID_ID3D11DeviceContext4, out _context4);
            if (hr < 0)
                return Fail(out failureReason, "QueryInterface(ID3D11DeviceContext4)", hr);
            hr = D3D12Backend.QueryInterface(_device, D3D11Backend.IID_ID3D11VideoDevice, out _videoDevice);
            if (hr < 0)
                return Fail(out failureReason, "QueryInterface(ID3D11VideoDevice)", hr);
            hr = D3D12Backend.QueryInterface(_context, D3D11Backend.IID_ID3D11VideoContext, out _videoContext);
            if (hr < 0)
                return Fail(out failureReason, "QueryInterface(ID3D11VideoContext)", hr);

            // The immediate context is used from the convert thread while the encoder's own
            // D3D11 calls come from the encode thread: the runtime's lock serializes them.
            // (FFmpeg's hwcontext enables the same protection at init; doing it here too keeps
            // the self-check below covered.)
            if (D3D12Backend.QueryInterface(_context, D3D11Backend.IID_ID3D11Multithread, out var multithread) >= 0
                || D3D12Backend.QueryInterface(_device, D3D11Backend.IID_ID3D11Multithread, out multithread) >= 0)
            {
                D3D11Backend.SetMultithreadProtected(multithread, true);
                D3D12Backend.Release(multithread);
            }

            for (int i = 0; i < _sharedTextures.Length; i++)
            {
                hr = D3D12Backend.CreateSharedHandle(_ring.Device, _ring.SlotTexture(i), out var handle);
                if (hr < 0)
                    return Fail(out failureReason, $"ID3D12Device::CreateSharedHandle(slot {i})", hr);
                hr = D3D11Backend.OpenSharedTexture(_device1, handle, out _sharedTextures[i]);
                D3D12Backend.CloseSharedHandle(handle);
                if (hr < 0)
                    return Fail(out failureReason, $"ID3D11Device1::OpenSharedResource1(slot {i})", hr);
            }

            if (!OpenFence(_ring.RenderFence, "render fence", out _renderFence, out failureReason)
                || !OpenFence(_ring.ConsumedFence, "consumed fence", out _consumedFence, out failureReason))
                return false;

            // ---------------------------------------------------- BGRA → NV12 video processor
            var content = new D3D11Backend.D3D11_VIDEO_PROCESSOR_CONTENT_DESC
            {
                InputFrameFormat = D3D11Backend.VideoFrameFormatProgressive,
                InputFrameRateNum = 60, // no rate conversion is asked of it; the rates only describe the content
                InputFrameRateDen = 1,
                InputWidth = (uint)w,
                InputHeight = (uint)h,
                OutputFrameRateNum = 60,
                OutputFrameRateDen = 1,
                OutputWidth = (uint)w,
                OutputHeight = (uint)h,
                Usage = D3D11Backend.VideoUsageOptimalQuality,
            };
            hr = D3D11Backend.CreateVideoProcessorEnumerator(_videoDevice, content, out _enumerator);
            if (hr < 0)
                return Fail(out failureReason, "ID3D11VideoDevice::CreateVideoProcessorEnumerator", hr);
            hr = D3D11Backend.CheckVideoProcessorFormat(_enumerator, D3D11Backend.FormatB8G8R8A8Unorm, out uint support);
            if (hr < 0 || (support & D3D11Backend.VideoProcessorFormatSupportInput) == 0)
                return Fail(out failureReason, "the video processor does not take B8G8R8A8_UNORM input", hr);
            hr = D3D11Backend.CheckVideoProcessorFormat(_enumerator, D3D11Backend.FormatNv12, out support);
            if (hr < 0 || (support & D3D11Backend.VideoProcessorFormatSupportOutput) == 0)
                return Fail(out failureReason, "the video processor does not produce NV12 output", hr);
            hr = D3D11Backend.CreateVideoProcessor(_videoDevice, _enumerator, out _processor);
            if (hr < 0)
                return Fail(out failureReason, "ID3D11VideoDevice::CreateVideoProcessor", hr);

            // Full-range RGB in, BT.601 limited-range YCbCr out (the readback path's swscale
            // default), one progressive stream covering the whole frame, no driver enhancements.
            var full = new D3D11Backend.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            D3D11Backend.VideoProcessorSetStreamFrameFormat(_videoContext, _processor, 0, D3D11Backend.VideoFrameFormatProgressive);
            D3D11Backend.VideoProcessorSetStreamColorSpace(_videoContext, _processor, 0, D3D11Backend.ColorSpaceUsageProcessing);
            D3D11Backend.VideoProcessorSetOutputColorSpace(_videoContext, _processor,
                D3D11Backend.ColorSpaceUsageProcessing | D3D11Backend.ColorSpaceNominalRange16To235);
            D3D11Backend.VideoProcessorSetStreamAutoProcessingMode(_videoContext, _processor, 0, false);
            D3D11Backend.VideoProcessorSetStreamSourceRect(_videoContext, _processor, 0, full);
            D3D11Backend.VideoProcessorSetStreamDestRect(_videoContext, _processor, 0, full);
            D3D11Backend.VideoProcessorSetOutputTargetRect(_videoContext, _processor, full);

            for (int i = 0; i < _inputViews.Length; i++)
            {
                hr = D3D11Backend.CreateVideoProcessorInputView(_videoDevice, _sharedTextures[i], _enumerator, out _inputViews[i]);
                if (hr < 0)
                    return Fail(out failureReason, $"ID3D11VideoDevice::CreateVideoProcessorInputView(slot {i})", hr);
            }

            // ----------------------------------------------------------- FFmpeg-facing frames
            try
            {
                _frames = new HardwareFrames(_device, w, h, AllocatePoolTexture, ReleasePoolTexture,
                    "Direct3D 11 NV12 textures (VideoProcessor BGRA->NV12, BT.601 limited)");
            }
            catch (InvalidOperationException ex)
            {
                failureReason = ex.Message;
                return false;
            }

            beforeSelfCheck?.Invoke(this);
            return SelfCheck(out failureReason);
        }

        private bool OpenFence(IntPtr fence12, string what, out IntPtr fence11, out string failureReason)
        {
            int hr = D3D12Backend.CreateSharedHandle(_ring.Device, fence12, out var handle);
            if (hr < 0)
            {
                fence11 = IntPtr.Zero;
                return Fail(out failureReason, $"ID3D12Device::CreateSharedHandle({what})", hr);
            }
            hr = D3D11Backend.OpenSharedFence(_device5, handle, out fence11);
            D3D12Backend.CloseSharedHandle(handle);
            if (hr < 0)
                return Fail(out failureReason, $"ID3D11Device5::OpenSharedFence({what})", hr);
            failureReason = null;
            return true;
        }

        private static bool Fail(out string failureReason, string step, int hr)
        {
            failureReason = hr < 0 ? $"{step} failed (0x{hr:X8})" : step;
            return false;
        }

        // ---------------------------------------------------------------------- NV12 pool

        /// <summary>A pool texture: NV12, render-target bindable (the video processor's output
        /// view needs it) and shader-readable, plus its output view.</summary>
        private IntPtr AllocatePoolTexture()
        {
            var desc = new D3D11Backend.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)_ring.Width,
                Height = (uint)_ring.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = D3D11Backend.FormatNv12,
                SampleCount = 1,
                Usage = D3D11Backend.UsageDefault,
                BindFlags = D3D11Backend.BindRenderTarget | D3D11Backend.BindShaderResource,
            };
            int hr = D3D11Backend.CreateTexture2D(_device, desc, out var texture);
            if (hr < 0)
                throw new InvalidOperationException($"ID3D11Device::CreateTexture2D({_ring.Width}x{_ring.Height} NV12) failed (0x{hr:X8}).");
            hr = D3D11Backend.CreateVideoProcessorOutputView(_videoDevice, texture, _enumerator, out var view);
            if (hr < 0)
            {
                D3D12Backend.Release(texture);
                throw new InvalidOperationException($"ID3D11VideoDevice::CreateVideoProcessorOutputView failed (0x{hr:X8}).");
            }
            lock (_viewSync)
                _outputViews.Add(texture, view);
            return texture;
        }

        private void ReleasePoolTexture(IntPtr texture)
        {
            IntPtr view;
            lock (_viewSync)
            {
                if (_outputViews.Remove(texture, out view))
                    D3D12Backend.Release(view);
            }
            D3D12Backend.Release(texture);
        }

        // -------------------------------------------------------------------- per frame

        /// <summary>
        /// Converts the slot the ring submitted with <see cref="D3D12ReadbackRing.SubmitShared"/>
        /// (which returned <paramref name="renderFenceValue"/>) into a pooled NV12 texture and
        /// returns it as a frame for the encoder. Queues, in order on the D3D11 immediate
        /// context: a GPU wait for the render fence, the blit, a signal of the consumed fence
        /// with the same value, a flush — nothing here waits on the CPU. The ring's
        /// <see cref="D3D12ReadbackRing.ReleaseShared"/> for the slot may follow at once. One
        /// thread at a time.
        /// </summary>
        public HardwareFrame Convert(int slot, ulong renderFenceValue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)_inputViews.Length, nameof(slot));

            Check(D3D11Backend.Context4Wait(_context4, _renderFence, renderFenceValue), "ID3D11DeviceContext4::Wait");
            IntPtr texture = _frames.RentTexture();
            IntPtr outputView;
            lock (_viewSync)
                outputView = _outputViews[texture];

            int blit = D3D11Backend.VideoProcessorBlt(_videoContext, _processor, outputView, _inputViews[slot]);
            // The consumed signal goes out even when the blit was refused: the ring's slot is
            // waiting for exactly this value, and a refused blit should fail the render with a
            // reason, not park it on the fence timeout.
            int signal = D3D11Backend.Context4Signal(_context4, _consumedFence, renderFenceValue);
            D3D11Backend.ContextFlush(_context);
            if (signal >= 0)
                _lastSignaled = renderFenceValue;
            if (blit < 0 || signal < 0)
            {
                _frames.ReturnTexture(texture);
                Check(blit, "ID3D11VideoContext::VideoProcessorBlt");
                Check(signal, "ID3D11DeviceContext4::Signal");
            }

            return _frames.Wrap(texture);
        }

        // -------------------------------------------------------------------- self-check

        /// <summary>The BT.601 limited-range NV12 samples of an opaque colour: what the readback
        /// path's swscale produces, and what the video processor is configured to produce.</summary>
        internal static (double Y, double U, double V) Bt601Limited(SKColor color)
        {
            double r = color.Red, g = color.Green, b = color.Blue;
            double ey = 0.299 * r + 0.587 * g + 0.114 * b;
            double eu = (b - ey) / 1.772; // 0.5 / (1 - Kb)
            double ev = (r - ey) / 1.402; // 0.5 / (1 - Kr)
            return (16 + 219 * ey / 255, 128 + 224 * eu / 255, 128 + 224 * ev / 255);
        }

        /// <summary>One frame of a known colour through the whole path — Skia into the shared
        /// texture, the video processor, the NV12 pool texture — read back through a staging
        /// texture and checked at three points against the BT.601-limited expectation. Whatever
        /// throws once the slot has been submitted, slot 0 goes back to the ring idle (see
        /// <see cref="RecoverSelfCheckSlot"/>) before the exception reaches
        /// <see cref="TryCreate"/>.</summary>
        private bool SelfCheck(out string failureReason)
        {
            var probe = new SKColor(200, 60, 30); // Y 100.5, U 94.1, V 191.6; BT.709 would give Y 91, full range V 200
            var canvas = _ring.Begin(0);
            canvas.Clear(probe);
            ulong value;
            try
            {
                value = _ring.SubmitShared(0);
            }
            catch
            {
                // the render-fence Signal was refused: the slot is in flight with nothing to wait for
                RecoverSelfCheckSlot(signalQueued: false);
                throw;
            }

            HardwareFrame frame = null;
            bool converted = false, released = false;
            try
            {
                frame = Convert(0, value);
                converted = true;
                _ring.ReleaseShared(0);
                released = true;
                var (y, uv) = ReadNv12(frame.Texture);
                var expected = Bt601Limited(probe);
                int w = _ring.Width, h = _ring.Height;
                foreach (var (px, py) in new[] { (0, 0), (w - 1, h - 1), (w / 2, h / 2) })
                {
                    int ys = y[py * w + px];
                    int us = uv[(py / 2) * w + (px / 2) * 2];
                    int vs = uv[(py / 2) * w + (px / 2) * 2 + 1];
                    if (Math.Abs(ys - expected.Y) > SelfCheckTolerance || Math.Abs(us - expected.U) > SelfCheckTolerance
                        || Math.Abs(vs - expected.V) > SelfCheckTolerance)
                    {
                        failureReason = $"the video processor produced YUV ({ys},{us},{vs}) at ({px},{py}) for RGB " +
                            $"({probe.Red},{probe.Green},{probe.Blue}), expected BT.601 limited ({expected.Y:F0},{expected.U:F0},{expected.V:F0})";
                        return false;
                    }
                }
            }
            finally
            {
                frame?.Dispose();
                // Convert threw (its consumed signal may or may not have gone out: _lastSignaled
                // says), or ReleaseShared did (the signal went out but never landed — a timeout
                // or a removed device — so waiting again is pointless).
                if (!released)
                    RecoverSelfCheckSlot(signalQueued: !converted && _lastSignaled >= value);
            }

            failureReason = null;
            return true;
        }

        /// <summary>
        /// Puts slot 0 back to idle after the self-check failed past
        /// <see cref="D3D12ReadbackRing.SubmitShared"/>. <see cref="TryCreate"/> turns that
        /// failure into a null bridge and the caller carries on with readback over the same
        /// ring, whose first <see cref="D3D12ReadbackRing.Begin"/> is on slot 0 — left in
        /// flight, that would fail every hardware-encoder render on a machine that should merely
        /// have fallen back, with a ring-state message in place of the real reason. When the
        /// consumed-fence signal was queued on the D3D11 context it is waited for like any
        /// frame's (the blit, if it ran, may still be reading the slot); otherwise nothing will
        /// ever signal it and the ring is told to give the slot up. Swallows its own failures:
        /// the exception that got here is the reason worth reporting.
        /// </summary>
        private void RecoverSelfCheckSlot(bool signalQueued)
        {
            if (signalQueued)
            {
                try
                {
                    _ring.ReleaseShared(0);
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    // the signal never landed (device removed, GPU hung): fall through and give the slot up
                }
            }

            try
            {
                _ring.AbandonShared(0);
            }
            catch (InvalidOperationException)
            {
                // a fence the runtime refuses to signal: the ring is in no state to fall back on anyway
            }
        }

        /// <summary>Reads an NV12 pool texture back to the CPU (a staging copy — slow, for the
        /// self-check and tests only): the Y plane (<c>Width * Height</c> bytes) and the
        /// interleaved UV plane (<c>Width * Height / 2</c> bytes), tightly packed. Waits for
        /// every queued conversion to complete first.</summary>
        internal (byte[] Y, byte[] Uv) ReadNv12(IntPtr texture)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int w = _ring.Width, h = _ring.Height;
            var desc = new D3D11Backend.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = D3D11Backend.FormatNv12,
                SampleCount = 1,
                Usage = D3D11Backend.UsageStaging,
                CPUAccessFlags = D3D11Backend.CpuAccessRead,
            };
            Check(D3D11Backend.CreateTexture2D(_device, desc, out var staging), "ID3D11Device::CreateTexture2D(staging NV12)");
            try
            {
                D3D11Backend.ContextCopyResource(_context, staging, texture);
                Check(D3D11Backend.ContextMapRead(_context, staging, out var mapped), "ID3D11DeviceContext::Map(staging)");
                try
                {
                    var y = new byte[w * h];
                    var uv = new byte[w * h / 2];
                    byte* src = (byte*)mapped.pData;
                    for (int row = 0; row < h; row++)
                        new ReadOnlySpan<byte>(src + (long)row * mapped.RowPitch, w).CopyTo(y.AsSpan(row * w, w));
                    // the chroma plane follows the luma plane at RowPitch * Height in a mapped NV12 subresource
                    byte* chroma = src + (long)mapped.RowPitch * h;
                    for (int row = 0; row < h / 2; row++)
                        new ReadOnlySpan<byte>(chroma + (long)row * mapped.RowPitch, w).CopyTo(uv.AsSpan(row * w, w));
                    return (y, uv);
                }
                finally
                {
                    D3D11Backend.ContextUnmap(_context, staging);
                }
            }
            finally
            {
                D3D12Backend.Release(staging);
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Every blit this bridge queued must have finished reading the ring's textures and
            // writing the pool's before either goes away; the last consumed-fence signal is
            // ordered behind all of them.
            if (_lastSignaled > 0)
            {
                try
                {
                    _ring.WaitConsumed(_lastSignaled);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ObjectDisposedException)
                {
                    // a removed device or a ring already torn down: nothing left to wait for
                }
            }

            // The pool releases its textures (and their output views) here; the encoder that
            // referenced them must already be closed — the caller's ordering contract.
            _frames?.Dispose();
            _frames = null;
            lock (_viewSync)
            {
                foreach (var view in _outputViews.Values)
                    D3D12Backend.Release(view);
                _outputViews.Clear();
            }

            for (int i = 0; i < _inputViews.Length; i++)
            {
                D3D12Backend.Release(_inputViews[i]);
                D3D12Backend.Release(_sharedTextures[i]);
                _inputViews[i] = _sharedTextures[i] = IntPtr.Zero;
            }
            D3D12Backend.Release(_processor);
            D3D12Backend.Release(_enumerator);
            D3D12Backend.Release(_consumedFence);
            D3D12Backend.Release(_renderFence);
            D3D12Backend.Release(_videoContext);
            D3D12Backend.Release(_videoDevice);
            D3D12Backend.Release(_context4);
            D3D12Backend.Release(_device5);
            D3D12Backend.Release(_device1);
            D3D12Backend.Release(_context);
            D3D12Backend.Release(_device);
            _processor = _enumerator = _consumedFence = _renderFence = _videoContext = _videoDevice = IntPtr.Zero;
            _context4 = _device5 = _device1 = _context = _device = IntPtr.Zero;
        }
    }
}
