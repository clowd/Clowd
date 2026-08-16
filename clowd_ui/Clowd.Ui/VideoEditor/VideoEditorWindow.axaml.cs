using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.Config;
using Clowd.Drawing;
using Clowd.UI.Helpers;
using Clowd.UI.Services;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Path = System.IO.Path;
using Project = Clowd.VideoSDK.Model.Project;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The video editor window: in-process preview of a recording composed by the SDK
    /// (<c>CompositionPlayer</c> + <c>FrameComposer</c>, so the preview is the render), a
    /// multi-track edit owned by an <see cref="EditorSession"/> over the v2 <c>Project</c> loaded
    /// by <see cref="VideoEditPersistence.LoadOrCreate"/>, persisted through
    /// <see cref="EditorAutosave"/> as <c>videoedit.json</c> beside the session, and a Render
    /// button handing a snapshot of the edit to <see cref="VideoRenderManager"/>. Opened through
    /// <see cref="ShowSession"/> from the Recents page, or via the hidden dev arg
    /// <c>--video-edit file.mp4</c> (no session: persistence disabled, render output lands next
    /// to the file).
    ///
    /// There is one time domain now: the project's output timeline, which the timeline control,
    /// the readout and the player's transport all speak directly (the v1 source/timeline split —
    /// <c>EditTimeMap</c> — is gone). The session is the single mutation funnel: the timeline,
    /// the property inspector (<see cref="Inspector"/>) and the overlay gizmo all write through
    /// it, and this window reacts to <see cref="EditorSession.ProjectChanged"/> by handing the
    /// player a fresh snapshot.
    /// </summary>
    public partial class VideoEditorWindow : SystemThemedWindow
    {
        public const string ArgName = "--video-edit";

        private const double SidebarMinWidth = 140;
        private const double SidebarMaxWidth = 600;

        /// <summary>How long a text card or image lands on the timeline — long enough to see and
        /// to grab an edge of, and trimmable from there.</summary>
        private const long AddedItemDurationTicks = 5 * TimeSpan.TicksPerSecond;

        private const string EmptyEditMessage =
            "This edit keeps nothing of the recording. Undo, or add material back, to preview it.";

        private readonly SessionInfo _session; // null in --video-edit dev mode
        private readonly string _videoPath;
        private readonly string _editDocPath; // null when persistence is disabled (dev mode)
        private readonly bool _exitOnClose; // dev mode bypasses the tray lifetime entirely

        private CompositionPlayer _player;
        private EditorSession _editor;
        private EditorAutosave _autosave;
        private TimelinePreviewProvider _preview; // filmstrips + waveforms for the timeline rows
        private bool _scrubbing;
        private bool _wasPlayingBeforeScrub;
        private bool _sidebarVisible;
        private bool _sidebarUserClosed; // the user closed the properties panel — stop auto-showing it
        private bool _playerFailedShown; // the status overlay currently shows a player failure
        private bool _playerUpdatePending; // an edit arrived while the player was Opening — re-applied on Ready
        private bool _emptyEditShown; // the status overlay currently shows the empty-edit notice
        private bool _closing;

        // the session's debounced persistence: the session hands ScheduleSave its write callback,
        // this timer runs the latest one 500ms after the last edit (flushed on close).
        private DispatcherTimer _saveDebounce;
        private Action _pendingSave;

        // the render currently reflected on btnRender's progress ring. Session mode tracks the
        // edited entry's ActiveRender (the manager owns the run — the window may close during
        // it); dev mode runs a VidRenderRunner of its own.
        private SessionInfo _renderSession;
        private VideoRender _trackedRender;
        private VidRenderRunner _devRunner;
        private bool _devRenderRunning;

        private const string RenderTooltip = "Render edited video";
        private const string CancelRenderTooltip = "Cancel render";

        /// <summary>The speeds the transport's picker offers. Audio follows the speed with its
        /// pitch shifted (no time stretching), as in any player's speed menu.</summary>
        private static readonly double[] PlaybackRates = { 0.25, 0.5, 1.0, 1.5, 2.0, 4.0 };

        private readonly List<MenuItem> _speedItems = new List<MenuItem>();
        private double _playbackRate = 1.0; // survives player rebuilds (see OpenPlayerAsync)

        // the top bar's resolution picker: rebuilt from the project on every change (the native
        // entry follows the media, and a Custom… size has to join the list), so the flag keeps that
        // refresh from reading back as a user pick.
        private List<ResolutionOption> _resolutionOptions = new List<ResolutionOption>();
        private bool _syncingResolution;

        // the sidebar's ColumnDefinition (contentGrid column 3). Avalonia's XAML compiler does not
        // emit a field for an x:Named ColumnDefinition, so reach it through the named grid.
        private ColumnDefinition SidebarColumn => contentGrid.ColumnDefinitions[3];

        private static SettingsVideoEditor Settings => SettingsRoot.Current?.VideoEditor;

        public RelayCommand CommandPlayPause { get; }
        public RelayCommand CommandStepBack { get; }
        public RelayCommand CommandStepForward { get; }
        public RelayCommand CommandJumpStart { get; }
        public RelayCommand CommandJumpEnd { get; }
        public RelayCommand CommandSplit { get; }
        public RelayCommand CommandUndo { get; }
        public RelayCommand CommandRedo { get; }
        public RelayCommand CommandAddText { get; }
        public RelayCommand CommandAddImage { get; }
        public RelayCommand CommandImportMedia { get; }
        public RelayCommand CommandImportAudio { get; }
        public RelayCommand CommandRender { get; }

        /// <summary>The property sidebar's view model. Created eagerly (before the session exists)
        /// so the XAML can bind to it once and never re-resolve; <see cref="StartPlaybackAsync"/>
        /// hands it the session.</summary>
        public SelectedItemViewModel Inspector { get; } = new SelectedItemViewModel();

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
            CommandJumpStart = new RelayCommand { Executed = _ => JumpToStart(), Text = "Jump to start" };
            CommandJumpEnd = new RelayCommand { Executed = _ => JumpToEnd(), Text = "Jump to end" };
            CommandSplit = new RelayCommand { Executed = _ => SplitAtPlayhead(), Text = "_Split Every Track at Playhead", Gesture = new SimpleKeyGesture(Key.K, KeyModifiers.Control) };
            CommandUndo = new RelayCommand
            {
                Executed = _ => Undo(),
                CanExecute = _ => _editor is { CanUndo: true },
                Text = "_Undo",
                Gesture = new SimpleKeyGesture(Key.Z, KeyModifiers.Control),
            };
            CommandRedo = new RelayCommand
            {
                Executed = _ => Redo(),
                CanExecute = _ => _editor is { CanRedo: true },
                Text = "_Redo",
                Gesture = new SimpleKeyGesture(Key.Y, KeyModifiers.Control),
            };
            CommandAddText = new RelayCommand { Executed = _ => AddText(), Text = "Add _Text" };
            CommandAddImage = new RelayCommand { Executed = _ => _ = AddImageAsync(), Text = "Add _Image" };
            CommandImportMedia = new RelayCommand
            {
                Executed = _ => _ = ImportMediaAsync("Import media", MediaFileTypes.AnyMedia),
                Text = "_Import Media",
            };
            CommandImportAudio = new RelayCommand
            {
                Executed = _ => _ = ImportMediaAsync("Import audio", MediaFileTypes.Audio),
                Text = "Import _Audio",
            };
            CommandRender = new RelayCommand { Executed = _ => RenderClicked(), Text = "_Render" };

            DataContext = this;

            InitializeComponent();

            // the browsers' col-resize (bars + arrows), not the plain SizeWestEast — which
            // GridSplitter assigns to its own Cursor on attach, so the custom cursor has to sit
            // on the template's panel, where the innermost non-null cursor wins
            sidebarSplitter.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find("splitterZone") is InputElement zone)
                    zone.Cursor = DragCursors.ColResize;
            };
            // …and on the splitter itself (after its own attach-time assignment), because a drag
            // in progress captures the pointer and shows the captured control's cursor
            sidebarSplitter.AttachedToVisualTree += (_, _) => sidebarSplitter.Cursor = DragCursors.ColResize;

            // Modifier-carrying command gestures become Window.KeyBindings; bare gestures
            // (Space/Left/Right/Delete/Home/End) are routed by the tunnel KeyDown handler below.
            // Escape is deliberately not handled anywhere in the window: the timeline surface
            // uses it to cancel a drag in progress.
            AddCommandKeyBinding(CommandSplit);
            AddCommandKeyBinding(CommandUndo);
            AddCommandKeyBinding(CommandRedo);
            KeyBindings.Add(new KeyBinding { Command = CommandRedo, Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift) });
            AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);

            timeline.ScrubStarted += Timeline_ScrubStarted;
            timeline.Scrubbed += Timeline_Scrubbed;
            timeline.ScrubCompleted += Timeline_ScrubCompleted;

            volumeIconHost.Children.Add(TimelineIcons.NewIcon("IconSpeakerEnabled", 16,
                new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))));

            // preview volume is per-session and always opens wide open, like a video player's:
            // it attenuates our own samples only (see NAudioSink.Volume), so there is nothing
            // here the user could have left turned down on the system.
            volumeSlider.Value = 1.0;
            volumeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && _player != null)
                    _player.Volume = volumeSlider.Value;
            };

            BuildSpeedMenu();

            ddResolution.PropertyChanged += Resolution_PropertyChanged;

            // the zoom readout is derived, not set: the preview owns the letterbox maths and
            // reports what it landed on (a resize, a Fit toggle or a resolution change all move it).
            preview.ZoomChanged += (_, _) => UpdateZoomReadout();
            UpdateZoomReadout();

            // the properties panel opens itself on the first selection (see AutoShowSidebar)
            ApplySidebarVisible(false);

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
        // Session + playback
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

            // probe first: the project (or the saved edit's reconciliation against the real file)
            // is built from it.
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

            Project project;
            try
            {
                // the session's recorder report names the audio rows a fresh edit creates ("Microphone"
                // rather than "Audio 2"); the probe still decides which rows there are.
                project = VideoEditPersistence.LoadOrCreate(_editDocPath, _videoPath, probe,
                    AudioTrackLabels.From(_session?.AudioTracks));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Video editor load failed: " + ex);
                SentryConfig.CaptureHandled(ex, "videoeditor.open");
                ShowStatus("Could not open the video: " + ex.Message);
                return;
            }

            _autosave = _editDocPath != null ? new EditorAutosave(_editDocPath) : null;
            _editor = new EditorSession(project, _autosave, ScheduleSave);

            // Before anything listens: a relink/remove/skip here is an ordinary session mutation,
            // and the ProjectChanged handler would open a player on a project the user is still
            // deciding about (and then be handed a second one below).
            await ResolveMissingSourcesAsync();
            if (_closing)
                return;

            _editor.ProjectChanged += Editor_ProjectChanged;
            _editor.HistoryChanged += (_, _) =>
            {
                CommandUndo.RaiseCanExecuteChanged();
                CommandRedo.RaiseCanExecuteChanged();
            };
            _editor.SelectionChanged += Editor_SelectionChanged;
            CommandUndo.RaiseCanExecuteChanged();
            CommandRedo.RaiseCanExecuteChanged();

            Inspector.Session = _editor;
            timeline.Session = _editor;

            // Filmstrips and waveforms decode on their own contexts, behind playback. Waveforms are
            // cached beside videoedit.json; the dev harness has no session directory and analyses
            // in memory (_editDocPath is null there).
            _preview = new TimelinePreviewProvider(_editor,
                _editDocPath != null ? Path.GetDirectoryName(_editDocPath) : null);
            // waveforms are queued before the first paint can queue any filmstrip work: the shared
            // decode thread runs one item at a time, and audio rows must not draw flat lines while
            // a whole-file keyframe pass (queued first only because video rows paint first) runs.
            _preview.Prime();
            timeline.PreviewProvider = _preview;
            timeline.Position = TimeSpan.Zero;

            // the preview follows the session itself: the gizmo it hosts is placed from the
            // selected item's own transform on every project/selection/playhead change.
            preview.Session = _editor;
            preview.SetVideo(new Size(project.Output.WidthPx, project.Output.HeightPx));

            // resolution only: the duration lives on the transport readout, beside the playhead.
            RefreshResolutionPicker();

            if (_editor.DurationTicks <= 0)
            {
                // the saved edit keeps nothing — nothing to compose or play. The player opens
                // lazily (Editor_ProjectChanged) if an undo or edit brings material back.
                ShowEmptyEditStatus();
                return;
            }

            await OpenPlayerAsync();
        }

        /// <summary>Offers the missing-media dialog when the edit references files that are no
        /// longer where it left them (see <see cref="MissingMediaDialog"/>). Best-effort: a failure
        /// here must cost the prompt, never the edit — the render's own guard still refuses to run
        /// on an edit whose media is missing.</summary>
        private async Task ResolveMissingSourcesAsync()
        {
            try
            {
                await MissingMediaDialog.ShowAsync(this, _editor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Missing media dialog failed: " + ex);
                SentryConfig.CaptureHandled(ex, "videoeditor.missing-media");
            }
        }

        /// <summary>Creates the player and opens it on a snapshot of the current project.</summary>
        private async Task OpenPlayerAsync()
        {
            var snapshot = _editor.SnapshotForPlayer();
            preview.SetProject(snapshot);

            _player = new CompositionPlayer(a => Dispatcher.UIThread.Post(a));
            _player.Volume = volumeSlider.Value;
            _player.PlaybackRate = _playbackRate; // set before the open so the clock starts scaled
            _player.PositionChanged += Player_PositionChanged;
            _player.StateChanged += Player_StateChanged;
            preview.AttachPlayer(_player);

            try
            {
                // preview decodes at display resolution, not at output resolution (proxy
                // behaviour); the composer scales the rest of the way.
                await _player.OpenAsync(snapshot, new VideoOpenOptions { MaxPresentHeight = 1080 });
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

            _playerFailedShown = false; // the notice may belong to a failed instance just replaced
            HideStatus();
            UpdatePositionReadout(TimeSpan.Zero);
            UpdatePlayPauseButton();
        }

        /// <summary>The edited (output) timeline length — the one duration the timeline, the
        /// readout and the render all measure against.</summary>
        private TimeSpan Duration => TimeSpan.FromTicks(_editor?.DurationTicks ?? 0);

        /// <summary>True once a project is open and the transport is usable.</summary>
        private bool PlayerReady =>
            _player != null && _player.State is PlayerState.Paused or PlayerState.Playing or PlayerState.Ended;

        /// <summary>
        /// The one reaction to every committed (or previewed) session change: hand the player and
        /// the preview a fresh snapshot — never the live instance, the player retains the
        /// reference and reads it from background rebuild threads. A change that keeps the stream
        /// set is an atomic mapping swap inside the player; one that changes it (track
        /// hidden/muted, undo across such an edit) rebuilds pipelines on a background task, which
        /// this awaits before refreshing the shown frame. Structural changes additionally re-seek
        /// a paused player so the frame on screen matches the new model.
        /// </summary>
        private async void Editor_ProjectChanged(object sender, ProjectChangedEventArgs e)
        {
            if (_closing || _editor == null)
                return;

            UpdatePositionReadout(_player?.Position ?? TimeSpan.Zero);

            // the canvas size is editable (and undoable), so the letterbox and the picker follow the
            // model on every change rather than only at open.
            var output = _editor.Project.Output;
            preview.SetVideo(new Size(output.WidthPx, output.HeightPx));
            RefreshResolutionPicker();

            if (_editor.DurationTicks <= 0)
            {
                // an empty project is a legal (undoable) state, but there is nothing to compose:
                // the player keeps its last playable project and the overlay says why.
                ShowEmptyEditStatus();
                return;
            }

            if (_emptyEditShown)
            {
                _emptyEditShown = false;
                if (!_playerFailedShown)
                    HideStatus();
            }

            if (_player == null)
            {
                // the edit was empty when the window opened; material just came back.
                await OpenPlayerAsync();
                return;
            }

            if (_player.State == PlayerState.Failed)
            {
                // UpdateProject rejects a Failed player (it throws), so healing means a fresh
                // one: tear the failed instance down and re-open it on the current snapshot.
                _playerUpdatePending = false;
                preview.AttachPlayer(null);
                _player.Dispose();
                _player = null;
                await OpenPlayerAsync();
                return;
            }

            var positionTicks = Math.Clamp(_player.Position.Ticks, 0, _editor.DurationTicks);
            var snapshot = _editor.SnapshotForPlayer();
            preview.SetProject(snapshot);

            if (!PlayerReady)
            {
                // Opening: the timeline is live while the file loads, and the player is about to
                // finish priming on a pre-edit snapshot. Player_StateChanged re-applies the
                // dropped update once the transport is usable.
                _playerUpdatePending = true;
                return;
            }

            _playerUpdatePending = false;
            var update = _player.UpdateProject(snapshot);
            if (!update.IsCompleted)
            {
                // stream set changed: the rebuild runs on a background task. A failed rebuild
                // does not fault the task — it lands the player in Failed, which
                // Player_StateChanged surfaces; PlayerReady turns false below.
                try
                {
                    await update;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Video editor project update failed: " + ex);
                    return;
                }

                if (_closing || !PlayerReady)
                    return;
            }

            // Any committed edit while paused: re-seek so the decoded frame matches the new
            // mapping (the frame source is hold-last and no tick runs while paused — a trim that
            // pulled the playhead's material away would otherwise keep the stale frame on screen)
            // and the playhead/readout are clamped into the new duration. Preview (mid-gesture)
            // changes are excluded so a drag stays cheap; Playing is excluded because the next
            // tick's seam check re-syncs anyway, and seeking on every change would stutter.
            if (e.Kind != ProjectChangeKind.Preview && _player.State != PlayerState.Playing)
                _ = _player.SeekAsync(TimeSpan.FromTicks(positionTicks), SeekMode.Exact);
        }

        private void ShowEmptyEditStatus()
        {
            _emptyEditShown = true;
            ShowStatus(EmptyEditMessage);
        }

        private void Player_PositionChanged(object sender, EventArgs e)
        {
            if (_player == null || _closing)
                return;

            var position = _player.Position;
            if (!_scrubbing)
                timeline.Position = position;
            UpdatePositionReadout(position);
        }

        private void Player_StateChanged(object sender, PlayerState state)
        {
            UpdatePlayPauseButton();

            // follow the playhead only while playing — while paused the user owns the scroll
            timeline.FollowPlayhead = state == PlayerState.Playing;

            // A failed live rebuild (an edit while the source file went away, decoder init
            // failure) surfaces here as the Failed state rather than as an exception on the UI
            // thread. A failed player cannot take updates, so the next committed edit replaces it
            // with a fresh one (Editor_ProjectChanged); hide the notice if that heals.
            if (state == PlayerState.Failed)
            {
                _playerFailedShown = true;
                var reason = _player?.LastError?.Message;
                ShowStatus("Video playback failed" + (String.IsNullOrEmpty(reason) ? "." : ": " + reason));
            }
            else if (_playerFailedShown && state is PlayerState.Paused or PlayerState.Playing)
            {
                _playerFailedShown = false;
                HideStatus();
            }

            // an edit that arrived while the player was still Opening was never pushed
            // (UpdateProject rejects a not-yet-open player) — push it now that the transport can
            // take one. Structural so a paused player also re-seeks onto the new mapping; the
            // handler recomputes the snapshot from the session, so re-entering it is safe.
            if (_playerUpdatePending && !_closing && PlayerReady && _editor is { DurationTicks: > 0 })
            {
                _playerUpdatePending = false;
                Editor_ProjectChanged(this, new ProjectChangedEventArgs(ProjectChangeKind.Structural, this));
            }
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

        /// <summary>Playhead to the first frame. Playback is left running — the same as the Home
        /// key, which these buttons mirror.</summary>
        private void JumpToStart() => SeekTo(0);

        /// <summary>Playhead to the end of the timeline (the whole project's, so text or images
        /// running past the last video frame are included).</summary>
        private void JumpToEnd() => SeekTo(_editor?.DurationTicks ?? 0);

        private void SeekTo(long ticks)
        {
            if (!PlayerReady)
                return;

            _ = _player.SeekAsync(TimeSpan.FromTicks(ticks), SeekMode.Exact);
        }

        private void Timeline_ScrubStarted(object sender, EventArgs e)
        {
            _scrubbing = true;
            _wasPlayingBeforeScrub = _player?.State == PlayerState.Playing;
            _player?.Pause();
        }

        private void Timeline_Scrubbed(object sender, long ticks)
        {
            UpdatePositionReadout(TimeSpan.FromTicks(ticks));
            if (PlayerReady)
                _ = _player.SeekAsync(TimeSpan.FromTicks(ticks), SeekMode.Fast);
        }

        private void Timeline_ScrubCompleted(object sender, long ticks)
        {
            _scrubbing = false;
            _ = FinishScrubAsync(ticks);
        }

        private async Task FinishScrubAsync(long ticks)
        {
            if (!PlayerReady)
                return;

            try
            {
                await _player.SeekAsync(TimeSpan.FromTicks(ticks), SeekMode.Exact);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Scrub seek failed: " + ex.Message);
            }

            if (_wasPlayingBeforeScrub && !_closing)
                _player?.Play();
        }

        // ====================================================================
        // Edit commands
        // ====================================================================

        private void SplitAtPlayhead()
        {
            // same text-focus gate as Undo/Redo: Ctrl+K from an inspector field must not split
            // the timeline out from under someone typing (see IsTextEditorFocused).
            if (_editor == null || _editor.IsGestureActive || IsTextEditorFocused())
                return;

            timeline.SplitAtPlayhead();
        }

        private void Undo()
        {
            // a drag in progress owns the model (a gesture is open); history is off-limits
            if (_editor == null || _editor.IsGestureActive || IsTextEditorFocused())
                return;

            _editor.Undo();
        }

        private void Redo()
        {
            if (_editor == null || _editor.IsGestureActive || IsTextEditorFocused())
                return;

            _editor.Redo();
        }

        /// <summary>Where the toolbar's add/import puts new material: the playhead, clamped into
        /// the timeline (an empty project puts everything at the origin).</summary>
        private long PlayheadTicks =>
            Math.Clamp(timeline.Position.Ticks, 0, Math.Max(0, _editor?.DurationTicks ?? 0));

        /// <summary>True when the toolbar may add to the project. A gesture in progress owns the
        /// model — an add would ride the drag as an un-undoable preview.</summary>
        private bool CanAddToProject => _editor is { IsGestureActive: false } && !_closing;

        /// <summary>Adds a text card at the playhead and puts the user in the field its words live
        /// in — a card reading "Title" is not the point, typing over it is.</summary>
        private void AddText()
        {
            if (!CanAddToProject)
                return;

            var item = _editor.AddText(PlayheadTicks, AddedItemDurationTicks);
            if (item == null)
                return;

            RevealNewItem(item);

            SidebarVisible = true;
            // after the layout pass that the sidebar's own arrange runs in: a control that is not
            // yet effectively visible cannot take focus.
            Dispatcher.UIThread.Post(() => inspectorPanel.FocusText(), DispatcherPriority.Input);
        }

        private async Task AddImageAsync()
        {
            if (!CanAddToProject)
                return;

            var picked = await NiceDialog.ShowSelectFilesDialog(this, "Add image",
                filter: new[] { MediaFileTypes.Images, FilePickerFileTypes.All });
            if (picked is not { Length: > 0 } || !CanAddToProject)
                return;

            var item = _editor.AddImage(picked[0], PlayheadTicks, AddedItemDurationTicks);
            if (item != null)
                RevealNewItem(item);
        }

        /// <summary>Imports an external file as an overlay: probed off the UI thread (the same
        /// probe the recording itself goes through), then handed to the session, which builds the
        /// source, its rows and its items in one undo entry. The import-media and import-audio
        /// buttons are this one flow with a different picker title and filter — a video container
        /// picked through either maps the same streams.</summary>
        private async Task ImportMediaAsync(string title, FilePickerFileType filter)
        {
            if (!CanAddToProject)
                return;

            var picked = await NiceDialog.ShowSelectFilesDialog(this, title,
                filter: new[] { filter, FilePickerFileTypes.All });
            if (picked is not { Length: > 0 } || _closing)
                return;

            var path = picked[0];

            MediaProbeResult probe;
            try
            {
                probe = await Task.Run(() => MediaProbe.ProbeDetailed(path));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Import probe failed: " + ex);
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "That file could not be read: " + ex.Message, "Can't import this file");
                return;
            }

            if (!CanAddToProject)
                return;

            var created = _editor.ImportMedia(path, probe, PlayheadTicks);
            if (created.Count == 0)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "That file has no video or audio track to import.", "Can't import this file");
                return;
            }

            RevealNewItem(created[0]);
        }

        /// <summary>Selects what was just created and scrolls it into view — an add the user cannot
        /// see, on a row they then have to find, reads as an add that did not happen. (Selecting is
        /// also what opens the properties panel the first time.)</summary>
        private void RevealNewItem(Item item)
        {
            _editor.Select(item.Id);
            timeline.EnsureVisible(item.TimelineStartTicks, item.DurationTicks);
        }

        /// <summary>True when a text field owns the keyboard. Modifier-gesture commands that mutate
        /// the project (Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z / Ctrl+K) reach the session as window
        /// <see cref="Window.KeyBindings"/>, which are dispatched ahead of — and mark handled
        /// before — the tunnel handler's TextBox guard, so each of those commands gates on this —
        /// undoing (or splitting) the whole project out from under someone typing an inspector
        /// value (or a track name) is never what they meant, so the text box keeps its own
        /// history.</summary>
        private bool IsTextEditorFocused() => FocusManager?.GetFocusedElement() is TextBox;

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

            // the properties panel's combos/checkboxes/radios need Space (their one activation
            // key) and are not transport surfaces — while focus is inside the sidebar the focused
            // control keeps the keys. A type-based guard would misfire: every toolbar button is a
            // ToolButton (a ToggleButton), and Space re-firing the last-clicked one is exactly
            // what the blanket swallow exists to prevent.
            if (e.Source is Visual v && sidebarBorder.IsVisualAncestorOf(v))
                return;

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
                    if (timeline.DeleteSelection())
                        e.Handled = true;
                    return;
                case Key.Home:
                    JumpToStart();
                    e.Handled = true;
                    return;
                case Key.End:
                    JumpToEnd();
                    e.Handled = true;
                    return;
            }
        }

        // ====================================================================
        // Sidebar visibility + persistence scheduling
        // ====================================================================

        /// <summary>Toggles the right-hand properties sidebar. Per-window and transient like the
        /// image editor's layers sidebar — only its width is remembered. The toolbar toggle is the
        /// only writer; the auto-show path goes through <see cref="AutoShowSidebar"/>, which must
        /// not count as the user closing it.</summary>
        public bool SidebarVisible
        {
            get => _sidebarVisible;
            set
            {
                if (_sidebarVisible == value)
                    return;

                if (!value)
                    _sidebarUserClosed = true;

                _sidebarVisible = value;
                ApplySidebarVisible(value);
            }
        }

        /// <summary>Opens the properties panel the first time something is selected — the panel is
        /// where a selection becomes editable, so a user who has never dismissed it should not
        /// have to find the toggle. Once they close it, it stays closed for this window.</summary>
        private void Editor_SelectionChanged(object sender, EventArgs e) => AutoShowSidebar();

        private void AutoShowSidebar()
        {
            if (_sidebarVisible || _sidebarUserClosed || _closing ||
                _editor is not { SelectedItemIds.Count: > 0 })
                return;

            _sidebarVisible = true;
            ApplySidebarVisible(true);
        }

        private void ApplySidebarVisible(bool value)
        {
            sidebarBorder.IsVisible = value;
            sidebarSplitter.IsVisible = value;
            // the toggle binds two-way to SidebarVisible, but a plain CLR property on the window
            // raises no change notification — push the auto-show state onto the button by hand
            // (writing an unchanged value back through the binding is a no-op).
            btnInspector.IsChecked = value;

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

        /// <summary>The session's save scheduler: debounces the session's write callback by 500ms
        /// of quiet (the session is latest-wins internally, so running only the newest callback is
        /// correct). The close path flushes through <see cref="EditorSession.FlushSave"/> instead.</summary>
        private void ScheduleSave(Action save)
        {
            if (_closing)
            {
                save();
                return;
            }

            _pendingSave = save;
            _saveDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                _saveDebounce.Stop();
                var pending = _pendingSave;
                _pendingSave = null;
                pending?.Invoke();
            });
            _saveDebounce.Stop();
            _saveDebounce.Start();
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

            if (_editor == null)
                return;

            if (_editor.DurationTicks <= 0)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "This edit keeps nothing of the recording — undo, or add material back, and try again.",
                    "Can't render the video");
                return;
            }

            var missing = _editor.GetMissingSources();
            if (missing.Count > 0)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "A file this edit uses could not be found. It may have been moved or deleted:" +
                    Environment.NewLine + (String.IsNullOrEmpty(missing[0].Path) ? "(no path)" : missing[0].Path),
                    "Can't render the video");
                return;
            }

            if (_session == null)
            {
                await RunDevRenderAsync();
                return;
            }

            // a snapshot of the very project the preview is composing, so the render is what was
            // on screen (and later edits cannot race the render job file).
            var created = await VideoRenderManager.StartRenderAsync(_session, _editor.SnapshotForPlayer());
            if (created != null)
                TrackRenderSession(created);
        }

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

        /// <summary>Dev-mode render (--video-edit, no session): writes the same job file the manager
        /// would, puts the output next to the source file, and runs the render tool directly without
        /// creating any Recents entry.</summary>
        private async Task RunDevRenderAsync()
        {
            var project = _editor.SnapshotForPlayer();
            var outputPath = VideoRenderManager.GetOutputPath(_videoPath);
            var workDir = Path.Combine(Path.GetTempPath(), "clowd-video-edit-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(workDir);

                var argsPath = ProjectFileWriter.Write(
                    Path.Combine(workDir, VideoRenderManager.RenderArgsFileName), project, outputPath,
                    (int)(SettingsRoot.Current?.Recording?.Quality ?? VideoQuality.Medium));

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

            UpdateSidebarWidthSetting();
        }

        private void VideoEditorWindow_Closing(object sender, WindowClosingEventArgs e)
        {
            _closing = true;

            // flush the (debounced) edit before anything else — the edit is the work. FlushSave
            // hands the newest bytes to the autosave; Flush waits the disk write out.
            _saveDebounce?.Stop();
            _pendingSave = null;
            _editor?.FlushSave();
            _autosave?.Flush();

            SaveWindowState();
            TrySaveSettings();

            // an in-flight render is owned by VideoRenderManager and survives the window; only
            // detach the progress tracking.
            UntrackRender();

            // stop composing before the player goes away: a draw operation already queued would
            // otherwise reach a disposed frame source on the render thread.
            preview.AttachPlayer(null);
            preview.SetProject(null);

            // detach the timeline first: cancelling the decode passes releases the bitmaps the
            // rows draw from, so nothing may repaint after this point.
            timeline.PreviewProvider = null;
            _preview?.Dispose();
            _preview = null;

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

        /// <summary>The one funnel every position change passes through (playback ticks, scrubs,
        /// edits) — so it is also where the preview learns the playhead, which decides whether the
        /// selected item is on screen and therefore whether its gizmo is.</summary>
        private void UpdatePositionReadout(TimeSpan position)
        {
            txtPosition.Text = FormatTime(position) + " / " + FormatTime(Duration);
            preview.PositionTicks = position.Ticks;
        }

        /// <summary>Fills the speed button's drop-down once; the entries are radio items so the
        /// menu itself shows which speed is live, and the button's label repeats it closed.</summary>
        private void BuildSpeedMenu()
        {
            var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            foreach (var rate in PlaybackRates)
            {
                var item = new MenuItem
                {
                    Header = FormatRate(rate),
                    ToggleType = MenuItemToggleType.Radio,
                    GroupName = "videoEditorPlaybackRate",
                    IsChecked = rate == _playbackRate,
                    Tag = rate,
                };
                item.Click += (s, _) => SetPlaybackRate((double)((MenuItem)s).Tag);
                flyout.Items.Add(item);
                _speedItems.Add(item);
            }

            btnSpeed.Flyout = flyout;
            btnSpeed.Content = FormatRate(_playbackRate);
        }

        /// <summary>Applies a speed to the live player and to the picker. Kept on the window (not
        /// only on the player) so a pipeline rebuild or a re-opened player inherits it.</summary>
        private void SetPlaybackRate(double rate)
        {
            _playbackRate = rate;
            btnSpeed.Content = FormatRate(rate);
            foreach (var item in _speedItems)
                item.IsChecked = (double)item.Tag == rate;

            if (_player != null)
                _player.PlaybackRate = rate;
        }

        private static string FormatRate(double rate)
            => rate.ToString("0.##", CultureInfo.InvariantCulture) + "x";

        /// <summary>The preview's current magnification, as a percentage of the frame's own pixels.
        /// Whole percent only: a fractional digit changes the readout's width as the window is
        /// resized or Fit is toggled, which shoves the Fit box sideways. Blank until the frame size
        /// is known (an editor opened on an empty edit), and never rounded down to 0%.</summary>
        private void UpdateZoomReadout()
        {
            var zoom = preview.ZoomScale;
            txtZoom.Text = zoom > 0
                ? Math.Max(1, Math.Round(zoom * 100)).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "";
        }

        /// <summary>Rebuilds the resolution picker from the project and re-selects the size it is
        /// actually set to — the list depends on the media (the native entry) and on the current
        /// size, so undo, a Custom… size and an import all have to be able to change it.</summary>
        private void RefreshResolutionPicker()
        {
            if (_editor == null)
                return;

            _syncingResolution = true;
            try
            {
                _resolutionOptions = ResolutionOptions.Build(_editor.Project);
                ddResolution.ItemsSource = _resolutionOptions;
                ddResolution.SelectedItem = ResolutionOptions.FindCurrent(_resolutionOptions, _editor.Project);
            }
            finally
            {
                _syncingResolution = false;
            }
        }

        private void Resolution_PropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            // qualified: Avalonia has a DropDownButton of its own, and this is not it
            if (_syncingResolution || _editor == null ||
                e.Property != Clowd.UI.Controls.DropDownButton.SelectedItemProperty)
                return;

            if (e.GetNewValue<object>() is not ResolutionOption option)
                return;

            if (option.IsCustomPrompt)
            {
                _ = PromptCustomResolutionAsync();
                return;
            }

            // a committed resize raises ProjectChanged, which re-selects this entry anyway; a pick
            // of the size already set changes nothing and needs no refresh.
            _editor.SetOutputSize(option.WidthPx, option.HeightPx, this);
        }

        /// <summary>The "Custom…" row. The picker is showing that row as its label while the dialog
        /// is up, so the refresh at the end is what puts the real size back on the button —
        /// including when the user cancels.</summary>
        private async Task PromptCustomResolutionAsync()
        {
            var output = _editor.Project.Output;
            try
            {
                var size = await CustomResolutionDialog.ShowAsync(this, output.WidthPx, output.HeightPx);
                if (size != null && !_closing && _editor != null)
                    _editor.SetOutputSize(size.Value.WidthPx, size.Value.HeightPx, this);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Custom resolution dialog failed: " + ex);
                SentryConfig.CaptureHandled(ex, "videoeditor.custom-resolution");
            }

            if (!_closing)
                RefreshResolutionPicker();
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
