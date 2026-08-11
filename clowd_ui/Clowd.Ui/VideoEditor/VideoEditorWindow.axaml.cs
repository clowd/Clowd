using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.UI.Services;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Playback;
using Path = System.IO.Path;
using Project = Clowd.VideoSDK.Model.Project;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The video editor window: in-process preview of a recording composed by the SDK
    /// (<c>CompositionPlayer</c> + <c>FrameComposer</c>, so the preview is the render), a
    /// non-destructive edit (trim / cuts / webcam overlay) held as a <see cref="VideoEditDocument"/>
    /// and projected onto a v2 <c>Project</c> by <see cref="EditorProject"/>, persisted as the
    /// project itself in <c>videoedit.json</c> beside the session, and a Render button handing the
    /// edit to <see cref="VideoRenderManager"/>. Opened through <see cref="ShowSession"/> from the
    /// Recents page, or via the hidden dev arg <c>--video-edit file.mp4</c> (no session:
    /// persistence disabled, render output lands next to the file).
    ///
    /// Two time domains meet here. The timeline control, the position readout and the document all
    /// speak <b>source</b> time — the whole recording, including what the edit removes — while the
    /// player's transport speaks <b>output timeline</b> time, where the kept segments are back to
    /// back. <see cref="EditTimeMap"/> converts, and every transport call goes through
    /// <see cref="SeekToSource"/> / <see cref="CurrentSourcePosition"/> so the two cannot drift.
    /// </summary>
    public partial class VideoEditorWindow : SystemThemedWindow
    {
        public const string ArgName = "--video-edit";

        private const double SidebarMinWidth = 140;
        private const double SidebarMaxWidth = 600;
        private const long DefaultCutLengthMs = 2000;

        private readonly SessionInfo _session; // null in --video-edit dev mode
        private readonly string _videoPath;
        private readonly string _editDocPath; // null when persistence is disabled (dev mode)
        private readonly VideoEditDocument _document = new VideoEditDocument();
        private readonly bool _exitOnClose; // dev mode bypasses the tray lifetime entirely

        private CompositionPlayer _player;
        private EditorProject _edit;
        private bool _hasWebcamTrack;
        private bool _scrubbing;
        private bool _wasPlayingBeforeScrub;
        private bool _sidebarVisible;
        private bool _syncingShape;
        private bool _suspendEdits; // applying a loaded edit: no persist, no project rebuild
        private bool _closing;

        // videoedit.json persistence: debounced (500ms) on the UI thread, then written by a
        // latest-wins background chain; flushed synchronously on close. Same shape as the
        // graphics.json writer in EditorWindow.
        private DispatcherTimer _persistDebounce;
        private byte[] _pendingEditJson;
        private Task _editWriteTask = Task.CompletedTask;
        private readonly object _editWriteLock = new object();

        // the render currently reflected on btnRender's progress ring. Session mode tracks the
        // edited entry's ActiveRender (the manager owns the run — the window may close during
        // it); dev mode runs a VidRenderRunner of its own.
        private SessionInfo _renderSession;
        private VideoRender _trackedRender;
        private VidRenderRunner _devRunner;
        private bool _devRenderRunning;

        private const string RenderTooltip = "Render edited video";
        private const string CancelRenderTooltip = "Cancel render";

        // the sidebar's ColumnDefinition (contentGrid column 2). Avalonia's XAML compiler does not
        // emit a field for an x:Named ColumnDefinition, so reach it through the named grid.
        private ColumnDefinition SidebarColumn => contentGrid.ColumnDefinitions[2];

        private static SettingsVideoEditor Settings => SettingsRoot.Current?.VideoEditor;

        public RelayCommand CommandPlayPause { get; }
        public RelayCommand CommandStepBack { get; }
        public RelayCommand CommandStepForward { get; }
        public RelayCommand CommandAddCut { get; }
        public RelayCommand CommandRender { get; }

        /// <summary>The edit document; the sidebar binds through this (Document.Webcam.*).</summary>
        public VideoEditDocument Document => _document;

        // satisfies the XAML compiler's runtime-loader check (AVLN3001); an editor is only ever
        // constructed through ShowSession / TryHandleArgs.
        [Obsolete("Runtime-loader signature only — use VideoEditorWindow.ShowSession.", error: true)]
        public VideoEditorWindow()
        {
            throw new NotSupportedException("VideoEditorWindow requires a video file.");
        }

        private VideoEditorWindow(SessionInfo session, string videoPath, bool exitOnClose)
        {
            _session = session;
            _videoPath = videoPath;
            _exitOnClose = exitOnClose;
            if (session != null)
                _editDocPath = Path.Combine(Path.GetDirectoryName(session.FilePath), VideoEditPersistence.FileName);

            CommandPlayPause = new RelayCommand { Executed = _ => TogglePlayPause(), Text = "Play/Pause" };
            CommandStepBack = new RelayCommand { Executed = _ => StepFrame(-1), Text = "Previous frame" };
            CommandStepForward = new RelayCommand { Executed = _ => StepFrame(1), Text = "Next frame" };
            CommandAddCut = new RelayCommand { Executed = _ => AddCutAtPlayhead(), Text = "Add _Cut", Gesture = new SimpleKeyGesture(Key.K, KeyModifiers.Control) };
            CommandRender = new RelayCommand { Executed = _ => RenderClicked(), Text = "_Render" };

            DataContext = this;

            // NB: the saved edit is restored in StartPlaybackAsync, not here — resolving a v2
            // project (or a v1 trim-end sentinel) back onto the document needs the probed
            // duration. The document raises change notifications, so the sidebar bindings and the
            // timeline pick the loaded values up as soon as they land.

            InitializeComponent();

            timeline.Document = _document;
            preview.Document = _document;

            _document.PropertyChanged += Document_PropertyChanged;
            _document.Webcam.PropertyChanged += Webcam_PropertyChanged;

            // Modifier-carrying command gestures become Window.KeyBindings; bare gestures
            // (Space/Left/Right/Delete/Home/End) are routed by the tunnel KeyDown handler below.
            AddCommandKeyBinding(CommandAddCut);
            AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);

            timeline.ScrubStarted += Timeline_ScrubStarted;
            timeline.Scrubbed += Timeline_Scrubbed;
            timeline.ScrubCompleted += Timeline_ScrubCompleted;

            radioCircle.IsCheckedChanged += (_, _) =>
            {
                if (!_syncingShape && radioCircle.IsChecked == true)
                    _document.Webcam.Shape = WebcamOverlayShape.Circle;
            };
            radioRounded.IsCheckedChanged += (_, _) =>
            {
                if (!_syncingShape && radioRounded.IsChecked == true)
                    _document.Webcam.Shape = WebcamOverlayShape.RoundedRect;
            };
            SyncShapeControls();

            volumeSlider.Value = Math.Clamp(Settings?.Volume ?? 1.0, 0, 1);
            volumeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && _player != null)
                    _player.Volume = volumeSlider.Value;
            };

            // the webcam panel is useless until we know the file actually has a webcam track
            ApplySidebarVisible(false);
            btnWebcamSidebar.IsEnabled = false;

            txtSessionName.Text = !String.IsNullOrEmpty(session?.Name) ? session.Name : Path.GetFileName(videoPath);

            RestoreWindowBounds();

            Opened += VideoEditorWindow_Opened;
            Closing += VideoEditorWindow_Closing;
        }

        // ====================================================================
        // Entry points
        // ====================================================================

        /// <summary>Opens (or focuses) the editor for a recording session. Falls back to the OS
        /// video player with a notice when in-process playback is unavailable.</summary>
        public static void ShowSession(SessionInfo session)
        {
            if (session == null || !session.CanEditVideo)
                return;

            // check if there is already a window open with this session in it
            var openWnd = GetOpenEditors().FirstOrDefault(w => ReferenceEquals(w._session, session));
            if (openWnd != null)
            {
                if (openWnd.WindowState == WindowState.Minimized)
                    openWnd.WindowState = WindowState.Normal;
                openWnd.Activate();
                return;
            }

            var videoPath = session.VideoPath;
            if (String.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                _ = NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "The recording could not be found. It may have been moved or deleted.",
                    "Can't edit the video");
                return;
            }

            if (!OperatingSystem.IsWindows() || !FFmpegLoader.TryInitialize(ResolveFFmpegDirectory))
            {
                // no in-app playback available — hand the file to the OS player so the user still
                // sees their recording, and say why the editor did not open.
                try
                {
                    Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to shell-open video: " + ex.Message);
                }

                var reason = OperatingSystem.IsWindows()
                    ? FFmpegLoader.FailureReason
                    : "the editor is only available on Windows";
                _ = NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    $"The built-in video editor is not available ({reason}). The recording has been opened in your default video player instead.",
                    "Can't edit the video");
                return;
            }

            var wnd = new VideoEditorWindow(session, videoPath, exitOnClose: false);
            wnd.Show();
            wnd.Activate();
        }

        /// <summary>Returns true (and takes over startup) when args request the hidden dev editor
        /// (<c>--video-edit file.mp4</c>): the editor opens on an arbitrary file with NO session —
        /// persistence disabled, render output next to the file. Must run before the
        /// single-instance mutex, exactly like the --video-spike harness.</summary>
        public static bool TryHandleArgs(string[] args)
        {
            if (args == null)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], ArgName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var window = new VideoEditorWindow(null, Path.GetFullPath(args[i + 1]), exitOnClose: true);
                    window.Show();
                    return true;
                }
            }

            return false;
        }

        internal static IEnumerable<VideoEditorWindow> GetOpenEditors()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.Windows.OfType<VideoEditorWindow>();
            return Enumerable.Empty<VideoEditorWindow>();
        }

        private static string ResolveFFmpegDirectory()
        {
            // production layout: the FFmpeg DLLs sit in the obs-express folder next to the exe;
            // dev machines set CLOWD_FFMPEG_PATH (checked by FFmpegLoader before this runs).
            var obs = ObsBinaryLocator.Resolve();
            return obs != null ? Path.GetDirectoryName(obs) : null;
        }

        // ====================================================================
        // Playback
        // ====================================================================

        private async void VideoEditorWindow_Opened(object sender, EventArgs e)
        {
            // poster first: the session's preview frame fills the letterbox until the first
            // decoded frame replaces it.
            var poster = _session?.PreviewImgPath;
            if (!String.IsNullOrEmpty(poster) && File.Exists(poster))
            {
                try { preview.PosterImage.Source = new Bitmap(poster); }
                catch {; }
            }

            // a window opened onto a recording whose previous render is still running shows that
            // render on the button immediately.
            if (_session != null)
            {
                var existing = VideoRenderManager.FindExisting(_session);
                if (existing?.ActiveRender != null)
                    TrackRenderSession(existing);
            }

            await StartPlaybackAsync();
        }

        private async Task StartPlaybackAsync()
        {
            if (!FFmpegLoader.TryInitialize(ResolveFFmpegDirectory))
            {
                ShowStatus("Video playback unavailable: " + FFmpegLoader.FailureReason);
                return;
            }

            ShowStatus("Loading video…");

            // probe first: the project (and the saved edit's resolution against the real duration)
            // is built from it, and it is what the render path is told about the source.
            MediaProbeResult probe;
            try
            {
                probe = await Task.Run(() => MediaProbe.ProbeDetailed(_videoPath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Video editor probe failed: " + ex);
                SentryConfig.CaptureHandled(ex, "videoeditor.open");
                ShowStatus("Could not open the video: " + ex.Message);
                return;
            }

            if (_closing)
                return;

            if (probe.VideoStreams == null || probe.VideoStreams.Count == 0)
            {
                ShowStatus("This file has no video track to edit.");
                return;
            }

            _edit = new EditorProject(_videoPath, probe, _document);

            // now that the duration is known, restore the saved edit (v1 files migrate here) and
            // build the project it describes.
            _suspendEdits = true;
            try
            {
                if (_editDocPath != null)
                    VideoEditPersistence.TryLoadInto(_editDocPath, _document, _edit.DurationMs);
            }
            finally
            {
                _suspendEdits = false;
            }

            SyncShapeControls();
            _edit.Rebuild();

            preview.SetVideo(new Size(_edit.ScreenWidth, _edit.ScreenHeight));

            _hasWebcamTrack = _edit.HasWebcam;
            preview.SetWebcam(_hasWebcamTrack, _edit.WebcamAspect);
            preview.SetProject(_edit.Current);

            // the webcam panel (and its toggle) only exists for files that carry a webcam track
            btnWebcamSidebar.IsEnabled = _hasWebcamTrack;
            if (!_hasWebcamTrack)
                SidebarVisible = false;

            timeline.Duration = SourceDuration;
            timeline.Position = TimeSpan.Zero;
            txtMediaSummary.Text = String.Create(CultureInfo.InvariantCulture,
                $"{_edit.ScreenWidth}x{_edit.ScreenHeight} · {FormatTime(SourceDuration)}");

            if (_edit.Current == null)
            {
                // the saved edit keeps nothing of the recording — nothing to compose or play.
                ShowStatus("This edit keeps nothing of the recording. Trim or cut less to preview it.");
                return;
            }

            _player = new CompositionPlayer(a => Dispatcher.UIThread.Post(a));
            _player.Volume = volumeSlider.Value;
            _player.PositionChanged += Player_PositionChanged;
            _player.StateChanged += Player_StateChanged;
            preview.AttachPlayer(_player);

            try
            {
                // preview decodes at display resolution, not at output resolution (proxy
                // behaviour); the composer scales the rest of the way.
                await _player.OpenAsync(_edit.Current, new VideoOpenOptions { MaxPresentHeight = 1080 });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Video editor open failed: " + ex);
                SentryConfig.CaptureHandled(ex, "videoeditor.open");
                ShowStatus("Could not open the video: " + ex.Message);
                return;
            }

            if (_closing)
                return; // closed while opening; Closing already disposed the player

            HideStatus();
            UpdatePositionReadout(TimeSpan.Zero);
            UpdatePlayPauseButton();
        }

        /// <summary>Length of the recording — the span the timeline draws and the domain every
        /// document value is expressed in. The one duration the preview, the timeline and the
        /// render args all measure against.</summary>
        private TimeSpan SourceDuration => TimeSpan.FromMilliseconds(_edit?.DurationMs ?? 0);

        /// <summary>True once a project is open and the transport is usable.</summary>
        private bool PlayerReady =>
            _player != null && _player.State is PlayerState.Paused or PlayerState.Playing or PlayerState.Ended;

        /// <summary>The playhead in source (recording) time — the domain the timeline, the readout
        /// and the document all speak.</summary>
        private TimeSpan CurrentSourcePosition()
        {
            if (_player == null || _edit == null)
                return TimeSpan.Zero;

            return TimeSpan.FromMilliseconds(
                _edit.TimeMap.ToSourceMs((long)_player.Position.TotalMilliseconds));
        }

        private void Player_PositionChanged(object sender, EventArgs e)
        {
            if (_player == null || _edit == null)
                return;

            var pos = CurrentSourcePosition();
            if (!_scrubbing)
                timeline.Position = pos;
            UpdatePositionReadout(pos);
        }

        private void Player_StateChanged(object sender, PlayerState state)
        {
            UpdatePlayPauseButton();
        }

        private void TogglePlayPause()
        {
            if (!PlayerReady)
                return;

            if (_player.State == PlayerState.Playing)
                _player.Pause();
            else
                _player.Play(); // rewinds automatically from Ended
        }

        private void StepFrame(int direction)
        {
            if (!PlayerReady)
                return;

            if (_player.State == PlayerState.Playing)
                _player.Pause();

            _ = _player.StepFrameAsync(direction);
        }

        /// <summary>Seeks to a <b>source</b> instant: a position inside a cut (or outside the trim)
        /// lands on the seam it collapsed to, which is where a render would jump too.</summary>
        private void SeekToSource(TimeSpan sourcePosition, SeekMode mode = SeekMode.Exact)
        {
            if (!PlayerReady || _edit == null)
                return;

            var timeline = _edit.TimeMap.ToTimelineMs((long)sourcePosition.TotalMilliseconds);
            _ = _player.SeekAsync(TimeSpan.FromMilliseconds(timeline), mode);
        }

        private void Timeline_ScrubStarted(object sender, EventArgs e)
        {
            _scrubbing = true;
            _wasPlayingBeforeScrub = _player?.State == PlayerState.Playing;
            _player?.Pause();
        }

        private void Timeline_Scrubbed(object sender, TimeSpan position)
        {
            UpdatePositionReadout(position);
            SeekToSource(position, SeekMode.Fast);
        }

        private void Timeline_ScrubCompleted(object sender, TimeSpan position)
        {
            _scrubbing = false;
            _ = FinishScrubAsync(position);
        }

        private async Task FinishScrubAsync(TimeSpan position)
        {
            if (!PlayerReady || _edit == null)
                return;

            try
            {
                var timelineMs = _edit.TimeMap.ToTimelineMs((long)position.TotalMilliseconds);
                await _player.SeekAsync(TimeSpan.FromMilliseconds(timelineMs), SeekMode.Exact);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Scrub seek failed: " + ex.Message);
            }

            if (_wasPlayingBeforeScrub && !_closing)
                _player?.Play();
        }

        private void AddCutAtPlayhead()
        {
            if (!PlayerReady || _edit == null)
                return;

            var durationMs = _edit.DurationMs;
            var startMs = (long)CurrentSourcePosition().TotalMilliseconds;
            var endMs = Math.Min(startMs + DefaultCutLengthMs, durationMs);
            // near the end of the media, grow the cut backwards so it is still a real cut
            if (endMs - startMs < VideoEditDocument.MinSegmentMs)
                startMs = Math.Max(0, endMs - DefaultCutLengthMs);

            _document.AddCut(startMs, endMs);
        }

        /// <summary>
        /// Rebuilds the project from the document and hands it to the player. Trim/cut/webcam edits
        /// keep the same source and streams, so this is an atomic mapping swap — no decoder is
        /// reopened, however fast the user drags. <paramref name="structural"/> edits (anything
        /// that moves material along the timeline) additionally re-seek, both to refresh a paused
        /// frame and to keep the playhead on the same source instant it was on before.
        /// </summary>
        private void ApplyEdit(bool structural)
        {
            if (_edit == null)
                return;

            var sourceMs = (long)CurrentSourcePosition().TotalMilliseconds;

            // an edit that keeps nothing leaves the last playable project in place; the render
            // path refuses it with a message of its own.
            if (!_edit.Rebuild())
                return;

            preview.SetProject(_edit.Current);

            if (!PlayerReady)
                return;

            _player.UpdateProject(_edit.Current);

            // Paused: re-seek so the shown frame is refreshed (it may be material the edit just
            // removed) and the playhead stays on the source instant it was on. Playing: leave it —
            // the player's own seam detection re-syncs the pipelines against the new mapping on
            // its next tick, and seeking on every pointer move of a drag would stutter.
            if (structural && _player.State != PlayerState.Playing)
            {
                var target = _edit.TimeMap.ToTimelineMs(sourceMs);
                _ = _player.SeekAsync(TimeSpan.FromMilliseconds(target), SeekMode.Exact);
            }
        }

        // ====================================================================
        // Keyboard
        // ====================================================================

        private void AddCommandKeyBinding(RelayCommand command)
        {
            var kb = command.CreateKeyBinding();
            if (kb == null)
                return;

            KeyBindings.Add(kb);
        }

        private void OnTunnelKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Source is TextBox)
                return; // spinner edits own the keyboard

            if (e.KeyModifiers != KeyModifiers.None)
                return;

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    return;
                case Key.Left:
                    StepFrame(-1);
                    e.Handled = true;
                    return;
                case Key.Right:
                    StepFrame(1);
                    e.Handled = true;
                    return;
                case Key.Delete:
                    if (timeline.DeleteSelectedCut())
                        e.Handled = true;
                    return;
                case Key.Home:
                    SeekToSource(TimeSpan.Zero);
                    e.Handled = true;
                    return;
                case Key.End:
                    SeekToSource(SourceDuration);
                    e.Handled = true;
                    return;
            }
        }

        // ====================================================================
        // Document changes, sidebar, persistence
        // ====================================================================

        private void Document_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_suspendEdits)
                return;

            SchedulePersist();

            // trim/cut move material along the output timeline: rebuild and re-seek.
            ApplyEdit(structural: true);
        }

        private void Webcam_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_suspendEdits)
                return;

            SchedulePersist();

            if (e.PropertyName == nameof(WebcamOverlay.Shape))
                SyncShapeControls();

            // geometry/shape only changes the picture; enabling or disabling the row changes which
            // streams the project references, which the player handles by rebuilding its pipeline.
            ApplyEdit(structural: e.PropertyName == nameof(WebcamOverlay.Enabled));
        }

        /// <summary>Keeps the shape radios and the corner-radius row in step with the document
        /// (the enum converter has no ConvertBack, so the radios are wired by hand).</summary>
        private void SyncShapeControls()
        {
            _syncingShape = true;
            try
            {
                var shape = _document.Webcam.Shape;
                radioCircle.IsChecked = shape == WebcamOverlayShape.Circle;
                radioRounded.IsChecked = shape == WebcamOverlayShape.RoundedRect;
                rowCornerRadius.IsVisible = shape == WebcamOverlayShape.RoundedRect;
            }
            finally
            {
                _syncingShape = false;
            }
        }

        /// <summary>Toggles the right-hand webcam sidebar. Per-window and transient like the image
        /// editor's layers sidebar — only its width is remembered.</summary>
        public bool SidebarVisible
        {
            get => _sidebarVisible;
            set
            {
                if (_sidebarVisible == value)
                    return;

                _sidebarVisible = value;
                ApplySidebarVisible(value);
            }
        }

        private void ApplySidebarVisible(bool value)
        {
            sidebarBorder.IsVisible = value;
            sidebarSplitter.IsVisible = value;

            // a pixel-width column reserves its space even when its content is collapsed; collapse
            // the column when hidden (Width AND MinWidth) and restore the persisted width shown.
            if (value)
            {
                SidebarColumn.MinWidth = SidebarMinWidth;
                SidebarColumn.Width = new GridLength(
                    Math.Clamp(Settings?.SidebarWidth ?? 230, SidebarMinWidth, SidebarMaxWidth), GridUnitType.Pixel);
            }
            else
            {
                SidebarColumn.MinWidth = 0;
                SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            }
        }

        private void sidebarSplitter_DragCompleted(object sender, VectorEventArgs e)
        {
            UpdateSidebarWidthSetting();
            TrySaveSettings();
        }

        private void UpdateSidebarWidthSetting()
        {
            if (!_sidebarVisible || Settings == null)
                return;

            Settings.SidebarWidth = Math.Clamp(sidebarBorder.Bounds.Width, SidebarMinWidth, SidebarMaxWidth);
        }

        private static void TrySaveSettings()
        {
            try
            {
                if (SettingsRoot.Current != null)
                    SettingsService.Save(SettingsRoot.Current);
            }
            catch {; }
        }

        private void SchedulePersist()
        {
            if (_editDocPath == null || _edit == null)
                return;

            _persistDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                _persistDebounce.Stop();
                EnqueueEditDocumentWrite();
            });
            _persistDebounce.Stop();
            _persistDebounce.Start();
        }

        /// <summary>Serializes on the UI thread (cheap), then hands the bytes to a latest-wins
        /// background writer so overlay drags can never stall the UI on disk writes.</summary>
        private void EnqueueEditDocumentWrite()
        {
            var project = CurrentProjectForSave();
            if (project == null)
                return;

            Interlocked.Exchange(ref _pendingEditJson, VideoEditPersistence.Serialize(project));
            lock (_editWriteLock)
                _editWriteTask = _editWriteTask.ContinueWith(_ => WritePendingEditJson(), TaskScheduler.Default);
        }

        /// <summary>The project to persist: the current one, or null when there is nothing to save
        /// (no session, or the media never opened — an unwritable edit must not truncate the file
        /// a previous session left behind).</summary>
        private Project CurrentProjectForSave() => _editDocPath == null ? null : _edit?.Current;

        private void WritePendingEditJson()
        {
            var bytes = Interlocked.Exchange(ref _pendingEditJson, null);
            if (bytes == null)
                return;

            try
            {
                File.WriteAllBytes(_editDocPath, bytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to persist videoedit.json: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "videoeditor.persist-doc");
            }
        }

        private void FlushEditDocument()
        {
            var project = CurrentProjectForSave();
            if (project == null)
                return;

            _persistDebounce?.Stop();
            Interlocked.Exchange(ref _pendingEditJson, VideoEditPersistence.Serialize(project));

            Task pendingWrite;
            lock (_editWriteLock)
                pendingWrite = _editWriteTask;
            try { pendingWrite.Wait(TimeSpan.FromSeconds(5)); }
            catch {; }

            WritePendingEditJson();
        }

        // ====================================================================
        // Render
        // ====================================================================

        private async void RenderClicked()
        {
            // while a render is in flight the button doubles as its cancel
            if (_trackedRender != null)
            {
                _trackedRender.Cancel();
                return;
            }

            if (_devRenderRunning)
            {
                var runner = _devRunner;
                if (runner != null)
                {
                    try { await runner.CancelAsync(); }
                    catch (Exception ex) { Debug.WriteLine("Dev render cancel failed: " + ex.Message); }
                }

                return;
            }

            if (_edit == null)
                return;

            if (_session == null)
            {
                await RunDevRenderAsync();
                return;
            }

            var created = await VideoRenderManager.StartRenderAsync(_session, _document, BuildRenderSource());
            if (created != null)
                TrackRenderSession(created);
        }

        /// <summary>What the render needs to know about the source media: duration, screen frame
        /// size, webcam stream + size. The same description the project was built from, so the
        /// rendered overlay rect is the one the preview composed.</summary>
        private VideoRenderSource BuildRenderSource() => _edit.RenderSource;

        /// <summary>Points btnRender's ring at <paramref name="renderSession"/>'s ActiveRender and
        /// keeps it in sync as the render finishes (the same tracking shape as the image editor's
        /// upload button).</summary>
        private void TrackRenderSession(SessionInfo renderSession)
        {
            if (!ReferenceEquals(_renderSession, renderSession))
            {
                if (_renderSession != null)
                    _renderSession.PropertyChanged -= RenderSession_PropertyChanged;

                _renderSession = renderSession;

                if (_renderSession != null)
                    _renderSession.PropertyChanged += RenderSession_PropertyChanged;
            }

            SyncRenderProgress();
        }

        private void RenderSession_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SessionInfo.ActiveRender))
                SyncRenderProgress();
        }

        private void SyncRenderProgress()
        {
            var render = _renderSession?.ActiveRender;
            if (ReferenceEquals(render, _trackedRender))
                return;

            if (_trackedRender != null)
                _trackedRender.PropertyChanged -= ActiveRender_PropertyChanged;

            _trackedRender = render;

            if (render != null)
            {
                render.PropertyChanged += ActiveRender_PropertyChanged;
                btnRender.Progress = render.Progress;
                btnRender.ShowProgress = true;
                ToolTip.SetTip(btnRender, CancelRenderTooltip);
            }
            else
            {
                btnRender.ShowProgress = false;
                btnRender.Progress = 0;
                ToolTip.SetTip(btnRender, RenderTooltip);
            }
        }

        private void ActiveRender_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VideoRender.Progress))
                btnRender.Progress = ((VideoRender)sender).Progress;
        }

        private void UntrackRender()
        {
            if (_renderSession != null)
                _renderSession.PropertyChanged -= RenderSession_PropertyChanged;
            _renderSession = null;

            if (_trackedRender != null)
                _trackedRender.PropertyChanged -= ActiveRender_PropertyChanged;
            _trackedRender = null;
        }

        /// <summary>Dev-mode render (--video-edit, no session): builds the same RenderArgs the
        /// manager would, writes the output next to the source file, and runs vid-render directly
        /// without creating any Recents entry.</summary>
        private async Task RunDevRenderAsync()
        {
            var segments = _document.GetKeepSegments(_edit.DurationMs);
            if (segments.Count == 0)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "This edit keeps nothing of the recording — trim or cut less and try again.",
                    "Can't render the video");
                return;
            }

            var outputPath = VideoRenderManager.GetOutputPath(_videoPath);
            var workDir = Path.Combine(Path.GetTempPath(), "clowd-video-edit-" + Guid.NewGuid().ToString("N"));

            var args = new RenderArgs
            {
                Input = _videoPath,
                Output = outputPath,
                Segments = RenderArgs.ToSegments(segments),
                Crf = (int)(SettingsRoot.Current?.Recording?.Quality ?? VideoQuality.Medium),
            };

            try
            {
                Directory.CreateDirectory(workDir);

                var src = BuildRenderSource();
                if (_document.Webcam.Enabled && src.HasWebcam)
                {
                    var rect = VideoRenderManager.ComputeWebcamRect(_document.Webcam, src);
                    var maskPath = Path.Combine(workDir, VideoRenderManager.MaskFileName);
                    WebcamMaskRenderer.WriteMask(maskPath, rect.W, rect.H, _document.Webcam);
                    args.Webcam = new RenderWebcam
                    {
                        StreamIndex = src.WebcamStreamIndex.Value,
                        Rect = rect,
                        MaskPng = maskPath,
                    };
                }

                var argsPath = Path.Combine(workDir, VideoRenderManager.RenderArgsFileName);
                File.WriteAllText(argsPath, args.ToJson());

                _devRunner = new VidRenderRunner();
                _devRunner.ProgressChanged += (_, percent) => btnRender.Progress = percent;
                _devRenderRunning = true;
                btnRender.ShowProgress = true;
                ToolTip.SetTip(btnRender, CancelRenderTooltip);

                var result = await _devRunner.RunAsync(argsPath);
                Console.WriteLine($"[video-edit] render result: {result.Outcome} {result.OutputPath} {result.Bytes}");

                switch (result.Outcome)
                {
                    case VidRenderOutcome.Success:
                        Toast.Show(this, "Video saved: " + Path.GetFileName(result.OutputPath ?? outputPath));
                        break;
                    case VidRenderOutcome.Cancelled:
                        break;
                    default:
                        await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error,
                            String.IsNullOrEmpty(result.Message) ? "The video render failed." : result.Message,
                            "Video render failed");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Dev render failed: " + ex);
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, ex.Message, "Video render failed");
            }
            finally
            {
                _devRunner?.Dispose();
                _devRunner = null;
                _devRenderRunning = false;
                if (!_closing)
                {
                    btnRender.ShowProgress = false;
                    btnRender.Progress = 0;
                    ToolTip.SetTip(btnRender, RenderTooltip);
                }

                try { Directory.Delete(workDir, recursive: true); }
                catch {; }
            }
        }

        // ====================================================================
        // Window state & shutdown
        // ====================================================================

        /// <summary>Restores the last window placement when it still intersects a connected
        /// screen (same format and rules as MainWindow.RestoreWindowBounds).</summary>
        private void RestoreWindowBounds()
        {
            var saved = Settings?.WindowBounds;
            if (String.IsNullOrEmpty(saved))
                return;

            var parts = saved.Split(',');
            if (parts.Length != 4
                || !Int32.TryParse(parts[0], out var x) || !Int32.TryParse(parts[1], out var y)
                || !Double.TryParse(parts[2], CultureInfo.InvariantCulture, out var w)
                || !Double.TryParse(parts[3], CultureInfo.InvariantCulture, out var h))
                return;

            if (w < MinWidth || h < MinHeight)
                return;

            var rect = new PixelRect(x, y, (int)w, (int)h);
            if (!Screens.All.Any(s => s.WorkingArea.Intersects(rect)))
                return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            Width = w;
            Height = h;

            if (Settings.WindowMaximized)
                WindowState = WindowState.Maximized;
        }

        private void SaveWindowState()
        {
            var settings = Settings;
            if (settings == null)
                return;

            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowBounds = String.Create(CultureInfo.InvariantCulture,
                    $"{Position.X},{Position.Y},{Width},{Height}");
            }

            settings.Volume = Math.Clamp(volumeSlider.Value, 0, 1);
            UpdateSidebarWidthSetting();
        }

        private void VideoEditorWindow_Closing(object sender, WindowClosingEventArgs e)
        {
            _closing = true;

            // flush the (debounced) edit document before anything else — the edit is the work
            FlushEditDocument();

            SaveWindowState();
            TrySaveSettings();

            // an in-flight render is owned by VideoRenderManager and survives the window; only
            // detach the progress tracking.
            UntrackRender();

            // stop composing before the player goes away: a draw operation already queued would
            // otherwise reach a disposed frame source on the render thread.
            preview.AttachPlayer(null);
            preview.SetProject(null);

            _player?.Dispose();
            _player = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // the dev harness bypasses the tray lifetime entirely; closing the window is exit.
            // (A dev render in flight dies with the process — it is a dev tool.)
            if (_exitOnClose)
                Environment.Exit(0);
        }

        // ====================================================================
        // Small UI helpers
        // ====================================================================

        private void ShowStatus(string text)
        {
            txtStatus.Text = text;
            statusOverlay.IsVisible = true;
        }

        private void HideStatus()
        {
            statusOverlay.IsVisible = false;
        }

        private void UpdatePositionReadout(TimeSpan position)
        {
            txtPosition.Text = FormatTime(position) + " / " + FormatTime(SourceDuration);
        }

        private void UpdatePlayPauseButton()
        {
            var playing = _player?.State == PlayerState.Playing;
            btnPlayPause.IconPath = FindIconGeometry(playing ? "IconPause" : "IconPlay");
            ToolTip.SetTip(btnPlayPause, playing ? "Pause (Space)" : "Play (Space)");
        }

        private Geometry FindIconGeometry(string key)
        {
            return this.TryFindResource(key, ActualThemeVariant, out var value) ? value as Geometry : null;
        }

        /// <summary>"mm:ss.f" with total minutes (the transport readout format, e.g. 01:30.0).</summary>
        internal static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
                t = TimeSpan.Zero;

            var totalMinutes = (long)t.TotalMinutes;
            return String.Create(CultureInfo.InvariantCulture,
                $"{totalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}");
        }
    }
}
