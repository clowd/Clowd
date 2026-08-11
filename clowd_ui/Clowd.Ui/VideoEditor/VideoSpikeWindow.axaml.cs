using System;
using System.IO;
using System.Text;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Hidden performance-spike harness for the in-process FFmpeg playback engine (U0 gate).
    /// Launched via <c>clowd --video-spike file.mp4</c> — see App.Startup, which short-circuits
    /// before the single-instance mutex. Presents 1-2 video tracks + audio with a stats overlay,
    /// and prints the same stats to stdout once per second (visible when stdout is redirected;
    /// Clowd.Ui is a WinExe).
    /// </summary>
    public partial class VideoSpikeWindow : SystemThemedWindow
    {
        public const string ArgName = "--video-spike";

        private readonly string _file;
        private FFmpegVideoPlayer _player;
        private VideoSpikeFrameSink _screenSink;
        private VideoSpikeFrameSink _webcamSink;
        private DispatcherTimer _statsTimer;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private double _spikeSeconds;
        private bool _dragging;

        public VideoSpikeWindow() : this(null)
        { }

        public VideoSpikeWindow(string file) : base(applyDefaultSize: false)
        {
            _file = file;
            InitializeComponent();
        }

        /// <summary>Returns true (and takes over startup) when args request the spike window.</summary>
        public static bool TryHandleArgs(string[] args)
        {
            if (args == null)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], ArgName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var window = new VideoSpikeWindow(Path.GetFullPath(args[i + 1]));
                    window.Show();
                    return true;
                }
            }

            return false;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (_file != null)
                _ = StartPlaybackAsync();
        }

        private async System.Threading.Tasks.Task StartPlaybackAsync()
        {
            if (!FFmpegLoader.TryInitialize(ResolveFFmpegDirectory))
            {
                StatsText.Text = "FFmpeg unavailable: " + FFmpegLoader.FailureReason;
                Console.WriteLine("[spike] FFmpeg unavailable: " + FFmpegLoader.FailureReason);
                return;
            }

            _screenSink = new VideoSpikeFrameSink(ScreenImage);
            _webcamSink = new VideoSpikeFrameSink(WebcamImage);

            _player = new FFmpegVideoPlayer(a => Dispatcher.UIThread.Post(a))
            {
                ScreenSink = _screenSink,
                WebcamSink = _webcamSink,
            };

            _player.PositionChanged += (s, _) => UpdateTransport();
            _player.StateChanged += (s, state) => UpdateTransport();

            try
            {
                // CLOWD_VIDEO_SPIKE_SW=1 forces the software decode path (fallback measurement).
                var swOnly = Environment.GetEnvironmentVariable("CLOWD_VIDEO_SPIKE_SW") == "1";
                var info = await _player.OpenAsync(_file,
                    new VideoOpenOptions { MaxPresentHeight = 1080, EnableHardwareDecode = !swOnly });
                WebcamBorder.IsVisible = info.VideoStreams.Count > 1;
                Title = $"Clowd Video Spike — {Path.GetFileName(_file)}";
                Console.WriteLine($"[spike] opened {Path.GetFileName(_file)}: " +
                                  $"{info.VideoStreams.Count} video stream(s), audio={info.HasAudio}, dur={info.Duration:mm\\:ss\\.ff}");
                foreach (var vs in info.VideoStreams)
                    Console.WriteLine($"[spike]   stream #{vs.StreamIndex}: {vs.Width}x{vs.Height} @ {vs.Fps:0.##} fps ({vs.CodecName})");
            }
            catch (Exception ex)
            {
                StatsText.Text = "Open failed: " + ex.Message;
                Console.WriteLine("[spike] open failed: " + ex);
                return;
            }

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += (s, _) => PrintStats();
            _statsTimer.Start();

            _player.Play();

            // CLOWD_VIDEO_SPIKE_SEEKTEST=1: scripted transport exercise (drag-style fast seeks,
            // exact seek, frame steps, skip ranges) for headless verification of the seek paths.
            if (Environment.GetEnvironmentVariable("CLOWD_VIDEO_SPIKE_SEEKTEST") == "1")
                _ = RunSeekScriptAsync();
        }

        private async System.Threading.Tasks.Task RunSeekScriptAsync()
        {
            try
            {
                var dur = _player.Info.Duration.TotalSeconds;
                await System.Threading.Tasks.Task.Delay(3000);

                Console.WriteLine("[spike] seektest: pause + drag (8 fast seeks)");
                _player.Pause();
                var rnd = new Random(42);
                for (int i = 0; i < 8; i++)
                {
                    var t = TimeSpan.FromSeconds(rnd.NextDouble() * dur * 0.9);
                    _ = _player.SeekAsync(t, SeekMode.Fast);
                    await System.Threading.Tasks.Task.Delay(150);
                }

                var exact = TimeSpan.FromSeconds(dur * 0.25);
                Console.WriteLine($"[spike] seektest: exact seek to {exact.TotalSeconds:0.00}s");
                await _player.SeekAsync(exact, SeekMode.Exact);
                Console.WriteLine($"[spike] seektest: exact done, pos={_player.Position.TotalSeconds:0.00}s");

                Console.WriteLine("[spike] seektest: step +1 x3, -1 x1");
                await _player.StepFrameAsync(1);
                await _player.StepFrameAsync(1);
                await _player.StepFrameAsync(1);
                Console.WriteLine($"[spike] seektest: after steps pos={_player.Position.TotalSeconds:0.000}s");
                await _player.StepFrameAsync(-1);
                Console.WriteLine($"[spike] seektest: after back-step pos={_player.Position.TotalSeconds:0.000}s");

                var skipStart = TimeSpan.FromSeconds(dur * 0.4);
                var skipEnd = TimeSpan.FromSeconds(dur * 0.6);
                Console.WriteLine($"[spike] seektest: skip range {skipStart.TotalSeconds:0.0}-{skipEnd.TotalSeconds:0.0}s, resume play");
                _player.SetSkipRanges(new[] { new TimeRange(skipStart, skipEnd) });
                _player.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[spike] seektest FAILED: " + ex);
            }
        }

        private static string ResolveFFmpegDirectory()
        {
            // production layout: the FFmpeg DLLs sit in the obs-express folder next to the exe;
            // dev machines set CLOWD_FFMPEG_PATH (checked by FFmpegLoader before this runs).
            var obs = ObsBinaryLocator.Resolve();
            return obs != null ? Path.GetDirectoryName(obs) : null;
        }

        private void PrintStats()
        {
            if (_player == null)
                return;

            _spikeSeconds += 1;
            var stats = _player.GetStatistics();
            var pos = _player.Position;

            _sb.Clear();
            _sb.Append("[spike] t=").Append(_spikeSeconds.ToString("0")).Append('s');
            _sb.Append(" state=").Append(_player.State);
            _sb.Append(" pos=").AppendFormat("{0:mm\\:ss\\.ff}", pos);

            for (int i = 0; i < stats.Video.Length; i++)
            {
                var t = stats.Video[i];
                _sb.Append(" | trk").Append(i)
                   .Append(' ').Append(t.IsHardware ? "HW" : "SW")
                   .Append(' ').Append(t.SourceWidth).Append('x').Append(t.SourceHeight)
                   .Append(" dec=").Append(t.DecodeMsPerFrame.ToString("0.0")).Append("ms")
                   .Append(" xfer=").Append(t.TransferMsPerFrame.ToString("0.0")).Append("ms")
                   .Append(" cvt=").Append(t.ConvertMsPerFrame.ToString("0.0")).Append("ms")
                   .Append(" fps=").Append(t.PresentedFps.ToString("0.0"))
                   .Append(" drop=").Append(t.DroppedTotal)
                   .Append(" q=").Append(t.FrameQueueDepth);
            }

            if (stats.HasAudio)
                _sb.Append(" | aud=").Append(stats.AudioBufferedSeconds.ToString("0.00")).Append('s');

            _sb.Append(" | gc=").Append(GC.CollectionCount(0))
               .Append('/').Append(GC.CollectionCount(1))
               .Append('/').Append(GC.CollectionCount(2));

            var line = _sb.ToString();
            Console.WriteLine(line);
            StatsText.Text = line.Replace(" | ", "\n").Substring("[spike] ".Length);
            UpdateTransport();
        }

        private void UpdateTransport()
        {
            var info = _player?.Info;
            if (info == null)
                return;

            var pos = _player.Position;
            var dur = info.Duration;
            TimeText.Text = $"{pos:mm\\:ss} / {dur:mm\\:ss}  [{_player.State}]";

            if (dur > TimeSpan.Zero && !_dragging)
            {
                var frac = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
                ProgressFill.Width = ProgressTrack.Bounds.Width * frac;
                ProgressFill.Height = ProgressTrack.Bounds.Height;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_player != null)
            {
                switch (e.Key)
                {
                    case Key.Space:
                        if (_player.State == PlayerState.Playing)
                            _player.Pause();
                        else
                            _player.Play();
                        e.Handled = true;
                        return;
                    case Key.Right:
                        _ = _player.StepFrameAsync(1);
                        e.Handled = true;
                        return;
                    case Key.Left:
                        _ = _player.StepFrameAsync(-1);
                        e.Handled = true;
                        return;
                }
            }

            base.OnKeyDown(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var p = e.GetPosition(ProgressTrack);
            if (p.Y >= 0 && p.Y <= ProgressTrack.Bounds.Height && p.X >= 0 && p.X <= ProgressTrack.Bounds.Width)
            {
                _dragging = true;
                SeekToPointer(p.X, SeekMode.Fast);
                e.Pointer.Capture(ProgressTrack);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragging)
                SeekToPointer(e.GetPosition(ProgressTrack).X, SeekMode.Fast);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragging)
            {
                _dragging = false;
                e.Pointer.Capture(null);
                SeekToPointer(e.GetPosition(ProgressTrack).X, SeekMode.Exact);
            }
        }

        private void SeekToPointer(double x, SeekMode mode)
        {
            var info = _player?.Info;
            if (info == null || info.Duration <= TimeSpan.Zero || ProgressTrack.Bounds.Width <= 0)
                return;

            var frac = Math.Clamp(x / ProgressTrack.Bounds.Width, 0, 1);
            var target = TimeSpan.FromSeconds(info.Duration.TotalSeconds * frac);
            ProgressFill.Width = ProgressTrack.Bounds.Width * frac;
            _ = _player.SeekAsync(target, mode);
        }

        protected override void OnClosed(EventArgs e)
        {
            _statsTimer?.Stop();
            _player?.Dispose();
            _screenSink?.Dispose();
            _webcamSink?.Dispose();
            base.OnClosed(e);

            // the spike bypasses the tray lifetime entirely; closing the window is exit.
            Environment.Exit(0);
        }
    }
}
