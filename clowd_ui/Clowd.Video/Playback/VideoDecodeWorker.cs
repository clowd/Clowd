using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// Decode + present pipeline for one video track.
    ///
    /// Decode thread: packets → avcodec (d3d11va first, automatic software fallback) →
    /// GPU→CPU NV12 download for hw frames → bounded <see cref="FrameQueue"/> (decode-ahead ~4).
    /// Present thread: takes frames in pts order, paces against the shared
    /// <see cref="PlaybackClock"/>, drops late frames, and sws_scales BGRA *directly into the
    /// sink's locked target* at presented size (<see cref="VideoOpenOptions.MaxPresentHeight"/>).
    /// Steady-state managed allocation is zero: AVPacket/AVFrame/SwsContext and the pointer
    /// arrays are all reused.
    /// </summary>
    internal sealed unsafe class VideoDecodeWorker : IDisposable
    {
        private readonly Demuxer _demuxer;
        private readonly int _streamIndex;
        private readonly PacketQueue _packets;
        private readonly FrameQueue _frames;
        private readonly VideoOpenOptions _options;
        private readonly PlaybackClock _clock;
        private readonly Func<IFrameSink> _sinkAccessor;
        private readonly Func<bool> _isPlaying;
        /// <summary>Invoked (decode-side threads) when a seek's immediate frame was presented.</summary>
        private readonly Action<VideoDecodeWorker, TimeSpan> _immediatePresented;

        private AVCodecContext* _ctx;
        private AVCodec* _codec;
        private AVBufferRef* _hwDeviceRef;
        private AVFrame* _decFrame;
        private AVFrame* _swFrame;
        private AVPacket* _pkt;
        private SwsContext* _sws;
        private AVCodecContext_get_format _getFormatDelegate; // rooted for the codec's lifetime
        private bool _isHw;
        private int _consecutiveErrors;

        // reused pointer arrays for sws_scale (no per-frame allocation)
        private readonly byte*[] _srcData = new byte*[4];
        private readonly int[] _srcStride = new int[4];
        private readonly byte*[] _dstData = new byte*[4];
        private readonly int[] _dstStride = new int[4];

        private Thread _decodeThread;
        private Thread _presentThread;
        private volatile bool _running;
        private readonly ManualResetEventSlim _presentWake = new ManualResetEventSlim(false);

        // seek/serial state
        private int _decodeSerial = -1;              // decode thread only
        private int _activeSerial = -1;              // published to present thread
        private long _exactDiscardTicks = long.MinValue; // decode thread; loaded on serial adoption
        private long _pendingSeekTargetTicks = long.MinValue;
        private int _pendingSeekExact;               // 1 = exact
        private int _presentImmediateOnce;           // present next valid frame regardless of clock
        private int _immediateMinSerial;             // guards immediate-present against stale frames
        private int _stepOnce;
        private TaskCompletionSource _stepTcs;
        private Action<TimeSpan> _stepPresented;
        private volatile bool _eofPresented;

        // timing/stats (Interlocked)
        private long _decodeTicks, _transferTicks, _convertTicks;
        private long _decodedCount, _presentedCount, _droppedTotal;
        private long _statsStampTicks;
        private long _lastPtsTicks = long.MinValue;
        private readonly long _frameDurTicks;
        private readonly long _dropThresholdTicks;
        private readonly AVRational _timeBase;
        private bool _disposed;

        public VideoDecodeWorker(
            Demuxer demuxer, int streamIndex, PacketQueue packets, VideoOpenOptions options,
            PlaybackClock clock, Func<IFrameSink> sinkAccessor, Func<bool> isPlaying,
            Action<VideoDecodeWorker, TimeSpan> immediatePresented)
        {
            _demuxer = demuxer;
            _streamIndex = streamIndex;
            _packets = packets;
            _options = options;
            _clock = clock;
            _sinkAccessor = sinkAccessor;
            _isPlaying = isPlaying;
            _immediatePresented = immediatePresented;
            _frames = new FrameQueue(4);

            var st = demuxer.GetStream(streamIndex);
            _timeBase = st->time_base;

            double fps = st->avg_frame_rate.den != 0 ? ffmpeg.av_q2d(st->avg_frame_rate) : 0;
            if (fps <= 0 && st->r_frame_rate.den != 0)
                fps = ffmpeg.av_q2d(st->r_frame_rate);
            if (fps <= 0)
                fps = 30;
            _frameDurTicks = (long)(TimeSpan.TicksPerSecond / fps);
            _dropThresholdTicks = Math.Max(_frameDurTicks, 10 * TimeSpan.TicksPerMillisecond);

            OpenDecoder(tryHardware: options.EnableHardwareDecode);

            _decFrame = ffmpeg.av_frame_alloc();
            _swFrame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
            _statsStampTicks = Stopwatch.GetTimestamp();
        }

        public int StreamIndex => _streamIndex;
        public bool IsHardware => _isHw;
        public bool EofPresented => _eofPresented;
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public TimeSpan FrameDuration => new TimeSpan(_frameDurTicks);

        private void OpenDecoder(bool tryHardware)
        {
            var st = _demuxer.GetStream(_streamIndex);
            _codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (_codec == null)
                throw new InvalidOperationException("No decoder for codec " + st->codecpar->codec_id);

            _ctx = ffmpeg.avcodec_alloc_context3(_codec);
            if (_ctx == null)
                throw new OutOfMemoryException("avcodec_alloc_context3 failed");

            int err = ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar);
            if (err < 0)
                throw new InvalidOperationException("avcodec_parameters_to_context: " + FFmpegLoader.ErrorToString(err));

            _ctx->pkt_timebase = st->time_base;
            SourceWidth = st->codecpar->width;
            SourceHeight = st->codecpar->height;

            bool hwReady = false;
            if (tryHardware)
            {
                AVBufferRef* device = null;
                err = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (err >= 0)
                {
                    _hwDeviceRef = device;
                    _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device);
                    _getFormatDelegate = GetFormatCallback;
                    _ctx->get_format = new AVCodecContext_get_format_func
                    {
                        Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatDelegate),
                    };
                    hwReady = true;
                }
            }

            if (!hwReady)
            {
                _ctx->thread_count = 0; // auto (frame+slice threading)
            }

            err = ffmpeg.avcodec_open2(_ctx, _codec, null);
            if (err < 0)
            {
                if (hwReady)
                {
                    // hardware path failed to open — throw the device away and go software.
                    CloseDecoder();
                    OpenDecoder(tryHardware: false);
                    return;
                }

                throw new InvalidOperationException("avcodec_open2: " + FFmpegLoader.ErrorToString(err));
            }

            _isHw = hwReady;
        }

        private AVPixelFormat GetFormatCallback(AVCodecContext* s, AVPixelFormat* fmts)
        {
            for (AVPixelFormat* p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
                    return *p;
            }

            // no d3d11 on offer — pick the first software format; the decoder proceeds in sw
            // inside the same context (frames simply arrive as sw frames).
            _isHw = false;
            return *fmts;
        }

        private void CloseDecoder()
        {
            if (_ctx != null)
            {
                var c = _ctx;
                ffmpeg.avcodec_free_context(&c);
                _ctx = null;
            }

            if (_hwDeviceRef != null)
            {
                var r = _hwDeviceRef;
                ffmpeg.av_buffer_unref(&r);
                _hwDeviceRef = null;
            }

            _getFormatDelegate = null;
            _isHw = false;
        }

        public void Start()
        {
            _running = true;
            _decodeThread = new Thread(DecodeLoop) { Name = $"clowd-vdec-{_streamIndex}", IsBackground = true };
            _presentThread = new Thread(PresentLoop) { Name = $"clowd-vpres-{_streamIndex}", IsBackground = true };
            _decodeThread.Start();
            _presentThread.Start();
        }

        /// <summary>Called by the controller *before* the demux flush: records what the decode
        /// thread should do when it adopts the post-seek serial.</summary>
        public void PrepareSeek(TimeSpan target, SeekMode mode)
        {
            Interlocked.Exchange(ref _pendingSeekTargetTicks, target.Ticks);
            Interlocked.Exchange(ref _pendingSeekExact, mode == SeekMode.Exact ? 1 : 0);
            Interlocked.Exchange(ref _immediateMinSerial, _packets.Serial + 1);
            Interlocked.Exchange(ref _presentImmediateOnce, 1);
        }

        /// <summary>Called by the controller *after* the demux flush: unblocks the pipeline and
        /// resets eof state.</summary>
        public void OnSeeked(int newSerial)
        {
            Interlocked.Exchange(ref _immediateMinSerial, newSerial);
            _eofPresented = false;
            _frames.Flush(); // also unblocks a decode thread parked in Reserve()
            WakePresent();
        }

        /// <summary>Present the next queued frame regardless of the clock (frame step).</summary>
        public void RequestStep(TaskCompletionSource tcs, Action<TimeSpan> presented)
        {
            _stepTcs = tcs;
            _stepPresented = presented;
            Interlocked.Exchange(ref _stepOnce, 1);
            WakePresent();
        }

        public void WakePresent() => _presentWake.Set();

        // ---------------- decode thread ----------------

        private void DecodeLoop()
        {
            while (_running)
            {
                if (!_packets.Get(_pkt, out int serial, out bool eof))
                    break; // stopped

                if (serial != _decodeSerial)
                    AdoptSerial(serial);

                if (eof)
                {
                    // drain the codec, then queue an EOF marker so the present thread knows the
                    // last real frame has passed through.
                    ffmpeg.avcodec_send_packet(_ctx, null);
                    ReceiveFrames(serial);
                    int slot = _frames.Reserve();
                    if (slot < 0)
                        break;
                    _frames.Commit(slot, serial, long.MaxValue, eof: true);
                    continue;
                }

                long t0 = Stopwatch.GetTimestamp();
                int ret = ffmpeg.avcodec_send_packet(_ctx, _pkt);
                Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - t0);
                ffmpeg.av_packet_unref(_pkt);

                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    // decoder wants frames pulled first; drain then drop this packet's resend —
                    // should not happen with our receive-after-every-send pattern.
                    ReceiveFrames(serial);
                    continue;
                }

                if (ret < 0)
                {
                    HandleDecodeError(ret);
                    continue;
                }

                _consecutiveErrors = 0;
                ReceiveFrames(serial);
            }
        }

        private void AdoptSerial(int serial)
        {
            ffmpeg.avcodec_flush_buffers(_ctx);
            _frames.Flush();
            _decodeSerial = serial;
            Interlocked.Exchange(ref _activeSerial, serial);
            _lastPtsTicks = long.MinValue;

            long target = Interlocked.Read(ref _pendingSeekTargetTicks);
            bool exact = Volatile.Read(ref _pendingSeekExact) == 1;
            _exactDiscardTicks = (target != long.MinValue && exact) ? target : long.MinValue;
        }

        private void ReceiveFrames(int serial)
        {
            while (_running)
            {
                long t0 = Stopwatch.GetTimestamp();
                int ret = ffmpeg.avcodec_receive_frame(_ctx, _decFrame);
                long t1 = Stopwatch.GetTimestamp();

                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    return;

                if (ret < 0)
                {
                    HandleDecodeError(ret);
                    return;
                }

                Interlocked.Add(ref _decodeTicks, t1 - t0);
                Interlocked.Increment(ref _decodedCount);
                _consecutiveErrors = 0;

                AVFrame* src = _decFrame;
                if (_decFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    // GPU→CPU download (NV12). ~1-2ms at 1440p on PCIe; measured as "transfer".
                    long tx0 = Stopwatch.GetTimestamp();
                    int terr = ffmpeg.av_hwframe_transfer_data(_swFrame, _decFrame, 0);
                    Interlocked.Add(ref _transferTicks, Stopwatch.GetTimestamp() - tx0);
                    if (terr < 0)
                    {
                        ffmpeg.av_frame_unref(_decFrame);
                        HandleDecodeError(terr);
                        continue;
                    }

                    ffmpeg.av_frame_copy_props(_swFrame, _decFrame);
                    ffmpeg.av_frame_unref(_decFrame);
                    src = _swFrame;
                }

                long ptsTicks = PtsToTicks(src);
                _lastPtsTicks = ptsTicks;

                if (_exactDiscardTicks != long.MinValue && ptsTicks + _frameDurTicks / 2 < _exactDiscardTicks)
                {
                    // exact seek: decode-forward from the keyframe, discard until the target.
                    ffmpeg.av_frame_unref(src);
                    continue;
                }

                _exactDiscardTicks = long.MinValue;

                int slot = _frames.Reserve();
                if (slot < 0)
                {
                    ffmpeg.av_frame_unref(src);
                    return;
                }

                ffmpeg.av_frame_move_ref(_frames.GetFrame(slot), src);
                _frames.Commit(slot, serial, ptsTicks, eof: false);
            }
        }

        private long PtsToTicks(AVFrame* frame)
        {
            long pts = frame->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                return _lastPtsTicks == long.MinValue ? 0 : _lastPtsTicks + _frameDurTicks;
            return (long)(pts * ffmpeg.av_q2d(_timeBase) * TimeSpan.TicksPerSecond);
        }

        private void HandleDecodeError(int error)
        {
            _consecutiveErrors++;
            if (_isHw && _consecutiveErrors >= 10)
            {
                // hardware decoding is misbehaving — fall back to software in place. Output
                // resumes cleanly at the next keyframe.
                CloseDecoder();
                OpenDecoder(tryHardware: false);
                _consecutiveErrors = 0;
            }
        }

        // ---------------- present thread ----------------

        private void PresentLoop()
        {
            while (_running)
            {
                int slot = _frames.PeekWait(100, out int serial, out long ptsTicks, out bool eof);
                if (slot < 0)
                    continue; // timeout or stopping; loop re-checks _running

                if (serial != Volatile.Read(ref _activeSerial))
                {
                    _frames.Release(slot); // stale pre-seek frame
                    continue;
                }

                if (eof)
                {
                    _eofPresented = true;
                    _frames.Release(slot);
                    // hold until new frames arrive (seek) — PeekWait blocks on the empty queue.
                    continue;
                }

                bool immediate = Volatile.Read(ref _presentImmediateOnce) == 1
                                 && serial >= Volatile.Read(ref _immediateMinSerial);
                bool step = Volatile.Read(ref _stepOnce) == 1;

                if (!immediate && !step)
                {
                    if (!_isPlaying())
                    {
                        // paused: hold the frame for later.
                        _presentWake.Reset();
                        _presentWake.Wait(100);
                        continue;
                    }

                    long now = _clock.Position.Ticks;
                    long wait = ptsTicks - now;
                    if (wait > 2 * TimeSpan.TicksPerMillisecond)
                    {
                        _presentWake.Reset();
                        int ms = (int)Math.Min(wait / TimeSpan.TicksPerMillisecond, 50);
                        _presentWake.Wait(ms);
                        continue; // re-evaluate: clock/seek/pause may have changed
                    }

                    if (now - ptsTicks > _dropThresholdTicks)
                    {
                        Interlocked.Increment(ref _droppedTotal);
                        _frames.Release(slot);
                        continue;
                    }
                }

                var pts = new TimeSpan(ptsTicks);
                Present(slot, pts);
                _frames.Release(slot);

                if (immediate)
                {
                    Interlocked.Exchange(ref _presentImmediateOnce, 0);
                    _immediatePresented?.Invoke(this, pts);
                }

                if (step)
                {
                    Interlocked.Exchange(ref _stepOnce, 0);
                    var cb = _stepPresented;
                    _stepPresented = null;
                    cb?.Invoke(pts);
                    var tcs = _stepTcs;
                    _stepTcs = null;
                    tcs?.TrySetResult();
                }
            }
        }

        private void Present(int slot, TimeSpan pts)
        {
            var sink = _sinkAccessor();
            if (sink == null)
            {
                Interlocked.Increment(ref _presentedCount);
                return; // no surface bound; treat as presented for pacing purposes
            }

            var frame = _frames.GetFrame(slot);
            int srcW = frame->width, srcH = frame->height;
            if (srcW <= 0 || srcH <= 0)
                return;

            ComputePresentSize(srcW, srcH, out int dstW, out int dstH);

            var target = sink.BeginFrame(dstW, dstH);
            if (target.Address == IntPtr.Zero)
                return;

            long c0 = Stopwatch.GetTimestamp();

            _sws = ffmpeg.sws_getCachedContext(_sws, srcW, srcH, (AVPixelFormat)frame->format,
                dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);

            for (uint i = 0; i < 4; i++)
            {
                _srcData[i] = frame->data[i];
                _srcStride[i] = frame->linesize[i];
            }

            _dstData[0] = (byte*)target.Address;
            _dstStride[0] = target.RowBytes;
            _dstData[1] = _dstData[2] = _dstData[3] = null;
            _dstStride[1] = _dstStride[2] = _dstStride[3] = 0;

            ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, srcH, _dstData, _dstStride);

            Interlocked.Add(ref _convertTicks, Stopwatch.GetTimestamp() - c0);

            sink.CompleteFrame(in target, pts);
            Interlocked.Increment(ref _presentedCount);
        }

        private void ComputePresentSize(int srcW, int srcH, out int dstW, out int dstH)
        {
            int max = _options.MaxPresentHeight;
            if (max <= 0 || srcH <= max)
            {
                dstW = srcW;
                dstH = srcH;
                return;
            }

            dstH = max;
            dstW = (int)Math.Round(srcW * (double)max / srcH) & ~1;
            if (dstW < 2)
                dstW = 2;
        }

        public TrackStatistics GetIntervalStatistics()
        {
            long nowStamp = Stopwatch.GetTimestamp();
            long stamp = Interlocked.Exchange(ref _statsStampTicks, nowStamp);
            double intervalSec = Math.Max((nowStamp - stamp) / (double)Stopwatch.Frequency, 0.001);

            long decoded = Interlocked.Exchange(ref _decodedCount, 0);
            long presented = Interlocked.Exchange(ref _presentedCount, 0);
            long decodeT = Interlocked.Exchange(ref _decodeTicks, 0);
            long transferT = Interlocked.Exchange(ref _transferTicks, 0);
            long convertT = Interlocked.Exchange(ref _convertTicks, 0);

            double toMs = 1000.0 / Stopwatch.Frequency;
            return new TrackStatistics
            {
                IsHardware = _isHw,
                SourceWidth = SourceWidth,
                SourceHeight = SourceHeight,
                DecodeMsPerFrame = decoded > 0 ? decodeT * toMs / decoded : 0,
                TransferMsPerFrame = decoded > 0 ? transferT * toMs / decoded : 0,
                ConvertMsPerFrame = presented > 0 ? convertT * toMs / presented : 0,
                PresentedFps = presented / intervalSec,
                DecodedInInterval = decoded,
                PresentedInInterval = presented,
                DroppedTotal = Interlocked.Read(ref _droppedTotal),
                FrameQueueDepth = _frames.ReadyCount,
            };
        }

        /// <summary>Stops both threads. The packet queue must already be stopped (or the decode
        /// thread would block in Get forever).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _running = false;
            _frames.Stop();
            _presentWake.Set();
            _decodeThread?.Join(3000);
            _presentThread?.Join(3000);

            CloseDecoder();

            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            if (_decFrame != null)
            {
                var f = _decFrame;
                ffmpeg.av_frame_free(&f);
                _decFrame = null;
            }

            if (_swFrame != null)
            {
                var f = _swFrame;
                ffmpeg.av_frame_free(&f);
                _swFrame = null;
            }

            if (_pkt != null)
            {
                var p = _pkt;
                ffmpeg.av_packet_free(&p);
                _pkt = null;
            }

            _frames.Dispose();
            _presentWake.Dispose();
        }
    }
}
