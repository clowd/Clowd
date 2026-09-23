using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.UI.Services;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Render;
using Ursa.Controls;

namespace Clowd.UI.VideoEditor
{
    /// <summary>What the render dialog needs to know about the project it is about to render: the
    /// canvas the edit composes into, the fastest frame rate it can be rendered at, and how long the
    /// edit runs. All of it is shown (the footer's "0:42 · 1920×1080 · 60 fps"); the canvas decides
    /// which size caps could shrink the output at all, and the rate which frame-rate caps could.</summary>
    /// <param name="WidthPx">Output canvas width in pixels.</param>
    /// <param name="HeightPx">Output canvas height in pixels.</param>
    /// <param name="FpsNum">Numerator of the frame-rate ceiling (<see cref="RenderFrameRate.Ceiling"/>):
    /// the fastest clip, what "Actual" renders at.</param>
    /// <param name="FpsDen">Its denominator.</param>
    /// <param name="Duration">How long the rendered video will be.</param>
    public readonly record struct RenderProjectInfo(int WidthPx, int HeightPx, int FpsNum, int FpsDen, TimeSpan Duration);

    /// <summary>
    /// The "Render video" dialog behind the Render flyout's "More options…" row: quality (a CRF
    /// slider, with the three presets as shortcuts onto it), an encode-time size cap, a frame-rate
    /// cap (the recording frame-rate presets, from settings), whether the
    /// GPU encoder may be used, the container (MP4 or MKV), where the file goes, and what should
    /// happen once it is there. It renders nothing itself — it returns a <see cref="RenderRequest"/>
    /// the caller starts, or null when the user backed out.
    ///
    /// H.264 and aac, x264 <c>fast</c>, is the only thing the renderer encodes today, so there is
    /// no codec or speed row: every row here is a choice that actually changes the file.
    /// </summary>
    public partial class RenderOptionsDialog : Window
    {
        private static readonly FilePickerFileType Mp4FileType = new FilePickerFileType("MP4 video")
        {
            Patterns = new[] { "*.mp4" },
            MimeTypes = new[] { "video/mp4" },
        };

        private static readonly FilePickerFileType MkvFileType = new FilePickerFileType("MKV video")
        {
            Patterns = new[] { "*.mkv" },
            MimeTypes = new[] { "video/x-matroska" },
        };

        private readonly RenderProjectInfo _project;

        // the rate "Actual" renders at, and the most any cap can be
        private readonly (int Num, int Den) _fpsCeiling;

        // the three middle cells of the Frame rate row and the rate each stands for (0: no preset
        // in that box, cell hidden)
        private readonly (RadioButton Cell, int Fps)[] _fpsPresetCells;
        private readonly string _defaultDirectory;

        // the request the dialog opened on. A cap this project cannot use (Share's 1080p on a 720p
        // canvas, its 60 fps on a 30 fps recording) opens as Actual; left there, the request keeps
        // the original cap — it renders identically, and the flyout can still tell it was Share.
        private readonly RenderRequest _initial;

        // an accept already in flight (the overwrite prompt is modal over this window, but Enter
        // can still be delivered here first)
        private bool _accepting;

        // what Close() was handed. ShowDialog carries its own result, a standalone Show() does not,
        // so the ownerless branch of ShowAsync reads the answer back from here instead of taking
        // every close for a cancel.
        private RenderRequest _result;

        // satisfies the XAML compiler's runtime-loader check (AVLN3001); the dialog is only ever
        // created through ShowAsync.
        [Obsolete("Runtime-loader signature only — use RenderOptionsDialog.ShowAsync.", error: true)]
        public RenderOptionsDialog()
        {
            throw new NotSupportedException("RenderOptionsDialog requires a project and a starting request.");
        }

        private RenderOptionsDialog(RenderProjectInfo project, RenderRequest initial, string defaultOutputPath,
            bool canDeleteSession)
        {
            _project = project;
            _initial = initial;
            _fpsCeiling = project.FpsNum > 0 && project.FpsDen > 0
                ? (project.FpsNum, project.FpsDen)
                : (RenderFrameRate.NoVideoCeilingFps, 1);

            InitializeComponent();
            Icon = AppStyles.AppIcon;

            _defaultDirectory = RenderOutputPath.DirectoryOf(defaultOutputPath);

            CrfSlider.Minimum = RenderPresets.MinCrf;
            CrfSlider.Maximum = RenderPresets.MaxCrf;

            // a cap that cannot shrink this project would be a lie — 720p on a 640x360 canvas
            // would have to upscale, which a render never does.
            Size1080.IsEnabled = _project.HeightPx > 1080;
            Size720.IsEnabled = _project.HeightPx > 720;
            Size480.IsEnabled = _project.HeightPx > 480;
            CustomHeightBox.Maximum = EditorSession.MaxOutputDimension;

            // the recording frame-rate presets (the FPS tile's), ascending and without the emptied boxes; like the size
            // cells, one at or above the fastest clip could not lower anything, so it is shown
            // but disabled.
            var presets = (SettingsRoot.Current?.Recording?.FpsPresets?.Values ?? Array.Empty<int>())
                .Where(f => f > 0).ToArray();
            _fpsPresetCells = new[] { FpsPreset1, FpsPreset2, FpsPreset3 }
                .Select((cell, i) => (cell, i < presets.Length ? presets[i] : 0))
                .ToArray();
            foreach (var (cell, fps) in _fpsPresetCells)
            {
                cell.IsVisible = fps > 0;
                cell.Content = fps.ToString(CultureInfo.InvariantCulture) + " fps";
                cell.IsEnabled = RenderFrameRate.IsBelow(fps, _fpsCeiling);
            }
            // a cap is whole frames per second, and the ceiling rounded up is the most one can be
            // (30 on 29.97 material, which then renders at the material's own rate)
            CustomFpsBox.Maximum = Math.Max(1, (int)Math.Ceiling(_fpsCeiling.Num / (double)_fpsCeiling.Den));

            SelectQuality(initial.Crf);
            SelectSize(initial.MaxHeight);
            SelectFps(initial.MaxFps);

            SelectContainer(initial.Container);
            PathBox.Text = RenderOutputPath.WithExtension(defaultOutputPath, initial.Container);
            HardwareCheck.IsChecked = initial.HardwareEncoder;
            CopyCheck.IsChecked = initial.CopyToClipboard;
            RevealCheck.IsChecked = initial.ShowInFolder;
            DeleteSessionCheck.IsChecked = initial.DeleteSession && canDeleteSession;
            DeleteSessionCheck.IsVisible = canDeleteSession;
            DeleteSessionWarning.IsVisible = DeleteSessionCheck.IsChecked == true;
            DeleteSessionCheck.IsCheckedChanged += (_, _) =>
                DeleteSessionWarning.IsVisible = DeleteSessionCheck.IsChecked == true;

            PresetNameBox.IsVisible = false;
            SavePresetCheck.IsCheckedChanged += (_, _) =>
            {
                PresetNameBox.IsVisible = SavePresetCheck.IsChecked == true;
                if (PresetNameBox.IsVisible)
                    PresetNameBox.Focus();
            };
            MetaText.Text = DescribeProject(_project);

            // the segments are shortcuts onto the slider: checking one moves it, and the slider
            // lights the segment its value amounts to (none, when it sits between them)
            foreach (var (segment, crf) in new[] { (QualityLow, (int)VideoQuality.Low), (QualityMedium, (int)VideoQuality.Medium), (QualityHigh, (int)VideoQuality.High) })
            {
                segment.IsCheckedChanged += (_, _) =>
                {
                    if (segment.IsChecked == true)
                        CrfSlider.Value = crf;
                };
            }
            CrfSlider.ValueChanged += (_, _) => SyncQualitySegments();
            foreach (var size in new[] { SizeActual, Size1080, Size720, Size480, SizeCustom })
                size.IsCheckedChanged += (_, _) => SyncSizeCaption();
            CustomHeightBox.ValueChanged += (_, _) => SyncSizeCaption();
            foreach (var fps in new[] { FpsActual, FpsPreset1, FpsPreset2, FpsPreset3, FpsCustom })
                fps.IsCheckedChanged += (_, _) => SyncFpsCaption();
            CustomFpsBox.ValueChanged += (_, _) => SyncFpsCaption();
            // the container is the path's extension: a cell renames the file, and a path that
            // arrives with the other extension (typed or picked) moves the check to match
            foreach (var cell in new[] { ContainerMp4, ContainerMkv })
                cell.IsCheckedChanged += (_, _) => SyncPathExtension();
            PathBox.TextChanged += (_, _) => SyncContainerFromPath();

            SyncQualitySegments();
            SyncSizeCaption();
            SyncFpsCaption();

            BrowseButton.Click += (_, _) => _ = BrowseAsync();
            RenderButton.Click += (_, _) => _ = AcceptAsync();
            CancelButton.Click += (_, _) => Close(null);

            // the path is the only thing here that is usually right already, so the dialog opens
            // on the quality row rather than dropping the caret into it.
            Opened += (_, _) => ((Control)CheckedQuality() ?? CrfSlider).Focus();

            // Cmd+W is the macOS close gesture — cancels, same as the Cancel button (issue #73)
            MacWindowShortcuts.AddCloseShortcut(this, () => Close(null));
        }

        /// <summary>
        /// Shows the dialog over <paramref name="owner"/> and returns the render the user asked
        /// for, or null when they canceled. <paramref name="initial"/> is what the rows start on
        /// (the last-used preset, or the last custom values), and
        /// <paramref name="defaultOutputPath"/> the full path the "Save to" box is pre-filled with.
        /// <paramref name="canDeleteSession"/> offers "Delete session", which needs a session to
        /// delete (the dev harness edits a bare file).
        /// </summary>
        public static async Task<RenderRequest> ShowAsync(Window owner, RenderProjectInfo project,
            RenderRequest initial, string defaultOutputPath, bool canDeleteSession)
        {
            ArgumentNullException.ThrowIfNull(initial);

#pragma warning disable CS0618 // the private constructor is the intended one
            var dialog = new RenderOptionsDialog(project, initial, defaultOutputPath, canDeleteSession);
#pragma warning restore CS0618

            if (owner is { IsVisible: true })
                return await dialog.ShowDialog<RenderRequest>(owner);

            // no owner to be modal over (the dev harness can be started without a main window):
            // show it standalone and resolve when it closes, as the resolution dialog does.
            var closed = new TaskCompletionSource<RenderRequest>();
            dialog.Closed += (_, _) => closed.TrySetResult(dialog._result);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            return await closed.Task;
        }

        /// <summary>The footer's meta line: how long the video is, the project's own output canvas
        /// and its frame rate. The cap's effect on the size is shown on the Size row itself, which
        /// is where the user is choosing it.</summary>
        private static string DescribeProject(RenderProjectInfo project) =>
            String.Format(CultureInfo.InvariantCulture, "{0} · {1}×{2} · {3} fps",
                FormatDuration(project.Duration), project.WidthPx, project.HeightPx, FormatFps(project));

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            return duration.TotalHours >= 1
                ? String.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                    (int)duration.TotalHours, duration.Minutes, duration.Seconds)
                : String.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", duration.Minutes, duration.Seconds);
        }

        /// <summary>"60", or "23.98" for the fractional rates NTSC material carries.</summary>
        private static string FormatFps(RenderProjectInfo project) =>
            RenderFrameRate.Format((project.FpsNum, project.FpsDen));

        /// <summary>The segment the slider currently sits on, or null when its value is none of
        /// the three.</summary>
        private RadioButton CheckedQuality()
        {
            if (QualityLow.IsChecked == true) return QualityLow;
            if (QualityMedium.IsChecked == true) return QualityMedium;
            if (QualityHigh.IsChecked == true) return QualityHigh;
            return null;
        }

        /// <summary>Puts the slider on <paramref name="crf"/>; the segments follow.</summary>
        private void SelectQuality(int crf) => CrfSlider.Value = RenderPresets.ClampCrf(crf);

        /// <summary>Lights the segment whose value the slider is on, clears them all when it is on
        /// a value of the user's own, and keeps the number beside the slider current.</summary>
        private void SyncQualitySegments()
        {
            var crf = SelectedCrf();
            QualityLow.IsChecked = crf == (int)VideoQuality.Low;
            QualityMedium.IsChecked = crf == (int)VideoQuality.Medium;
            QualityHigh.IsChecked = crf == (int)VideoQuality.High;
            CrfValue.Text = "CRF " + crf.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Checks the size segment for <paramref name="maxHeight"/>, falling back to Actual
        /// when that cap is one this project cannot use (a remembered 720p meeting a 720p project).
        /// Any other height (a remembered custom one, or the 1440p this row used to offer) opens on
        /// Custom with that height filled in; the Custom box otherwise starts on the project's own
        /// height, the most any cap can be.</summary>
        private void SelectSize(int maxHeight)
        {
            CustomHeightBox.Value = maxHeight is > 0 and not (1080 or 720 or 480)
                ? maxHeight
                : Math.Max(2, _project.HeightPx);

            if (maxHeight <= 0)
                SizeActual.IsChecked = true;
            else if (maxHeight == 1080)
                (Size1080.IsEnabled ? Size1080 : SizeActual).IsChecked = true;
            else if (maxHeight == 720)
                (Size720.IsEnabled ? Size720 : SizeActual).IsChecked = true;
            else if (maxHeight == 480)
                (Size480.IsEnabled ? Size480 : SizeActual).IsChecked = true;
            else
                SizeCustom.IsChecked = true;
        }

        private int SelectedCrf() => RenderPresets.ClampCrf((int)Math.Round(CrfSlider.Value));

        /// <summary>The whole number in a custom box, rounded (the box holds a double, so a typed
        /// fraction is possible); an empty box is 0.</summary>
        private static int WholeNumber(double? value) => value is { } v ? (int)Math.Round(v) : 0;

        private int SelectedMaxHeight() =>
            Size1080.IsChecked == true ? 1080 :
            Size720.IsChecked == true ? 720 :
            Size480.IsChecked == true ? 480 :
            SizeCustom.IsChecked == true ? Math.Max(0, WholeNumber(CustomHeightBox.Value)) : 0;

        /// <summary>The caption beside the size segments: what the encoder will actually be opened
        /// at, straight from the SDK's own rounding (even dimensions, aspect preserved) so the
        /// number here is the number in the file.</summary>
        private void SyncSizeCaption()
        {
            CustomHeightBox.IsVisible = SizeCustom.IsChecked == true;

            if (_project.WidthPx <= 0 || _project.HeightPx <= 0)
            {
                SizeCaption.Text = "";
                return;
            }

            var (width, height) = RenderJob.CapSize(_project.WidthPx, _project.HeightPx, SelectedMaxHeight());
            SizeCaption.Text = String.Format(CultureInfo.InvariantCulture, "{0}×{1}", width, height);
        }

        /// <summary>Checks the frame-rate cell for <paramref name="maxFps"/>, the same way
        /// <see cref="SelectSize"/> does: a preset cell when the cap is one of them and can lower
        /// this project, Actual when it cannot (a remembered 60 meeting a 30 fps project), Custom
        /// with the number filled in for any other cap. The Custom box otherwise starts on the
        /// ceiling, the most a cap can be.</summary>
        private void SelectFps(int maxFps)
        {
            var max = (int)CustomFpsBox.Maximum;
            var preset = _fpsPresetCells.FirstOrDefault(c => c.Fps > 0 && c.Fps == maxFps).Cell;

            CustomFpsBox.Value = maxFps > 0 && preset == null ? Math.Min(maxFps, max) : max;

            if (maxFps <= 0 || !RenderFrameRate.IsBelow(maxFps, _fpsCeiling))
                FpsActual.IsChecked = true;
            else if (preset != null)
                preset.IsChecked = true;
            else
                FpsCustom.IsChecked = true;
        }

        private int SelectedMaxFps()
        {
            if (FpsCustom.IsChecked == true)
                return RenderPresets.ClampFps(WholeNumber(CustomFpsBox.Value));

            foreach (var (cell, fps) in _fpsPresetCells)
            {
                if (cell.IsChecked == true)
                    return fps;
            }

            return 0;
        }

        /// <summary>The caption beside the frame-rate cells: the rate the file will actually be
        /// encoded at, through the same rule the render applies, so a cap at or above the fastest
        /// clip reads as the clip's own rate rather than as the number typed.</summary>
        private void SyncFpsCaption()
        {
            CustomFpsBox.IsVisible = FpsCustom.IsChecked == true;
            FpsCaption.Text = RenderFrameRate.Describe(RenderFrameRate.Resolve(_fpsCeiling, SelectedMaxFps()));
        }

        private VideoContainer SelectedContainer() =>
            ContainerMkv.IsChecked == true ? VideoContainer.Mkv : VideoContainer.Mp4;

        private void SelectContainer(VideoContainer container) =>
            (container == VideoContainer.Mkv ? ContainerMkv : ContainerMp4).IsChecked = true;

        /// <summary>Gives the "Save to" path the checked container's extension, when it names one
        /// of the two now (a path still being typed, with no extension yet, is left alone — the
        /// render adds it).</summary>
        private void SyncPathExtension()
        {
            var text = PathBox.Text;
            if (String.IsNullOrWhiteSpace(text))
                return;

            var container = SelectedContainer();
            var current = RenderOutputPath.ContainerOf(text.Trim());
            if (current != null && current != container)
                PathBox.Text = RenderOutputPath.WithExtension(text.Trim(), container);
        }

        /// <summary>A path typed or picked with the other container's extension checks that
        /// container.</summary>
        private void SyncContainerFromPath()
        {
            var current = RenderOutputPath.ContainerOf(PathBox.Text?.Trim());
            if (current != null && current != SelectedContainer())
                SelectContainer(current.Value);
        }

        private async Task BrowseAsync()
        {
            var current = ResolveTypedPath(out _);
            var directory = RenderOutputPath.DirectoryOf(current) ?? _defaultDirectory;

            var options = new FilePickerSaveOptions
            {
                Title = "Render video",
                SuggestedFileName = String.IsNullOrEmpty(current) ? null : Path.GetFileName(current),
                DefaultExtension = SelectedContainer().ToExtension().TrimStart('.'),
                ShowOverwritePrompt = true,
                // the checked container first, so it is the one the picker opens on
                FileTypeChoices = SelectedContainer() == VideoContainer.Mkv
                    ? new[] { MkvFileType, Mp4FileType }
                    : new[] { Mp4FileType, MkvFileType },
            };

            if (!String.IsNullOrEmpty(directory) && Directory.Exists(directory))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(directory);

            var picked = await StorageProvider.SaveFilePickerAsync(options);
            var path = picked?.TryGetLocalPath();
            if (!String.IsNullOrEmpty(path))
                PathBox.Text = RenderOutputPath.WithExtension(path, RenderOutputPath.ContainerOf(path) ?? SelectedContainer());
        }

        /// <summary>Validates the typed path, asks about an overwrite, and closes with the request
        /// when everything checks out. Anything the user has to fix leaves the dialog open with
        /// the reason said out loud.</summary>
        private async Task AcceptAsync()
        {
            // Enter can arrive again (or the button be clicked) while the overwrite prompt is up.
            if (_accepting)
                return;

            try
            {
                _accepting = true;
                await AcceptCoreAsync();
            }
            finally
            {
                _accepting = false;
            }
        }

        private async Task AcceptCoreAsync()
        {
            // a cleared Custom box is null, which would otherwise read as "no cap" and render at
            // full size behind a checked Custom
            if (SizeCustom.IsChecked == true && WholeNumber(CustomHeightBox.Value) <= 0)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "Enter the maximum height for the video, or pick one of the other sizes.",
                    "The custom size needs a height");
                CustomHeightBox.Focus();
                return;
            }

            if (FpsCustom.IsChecked == true && WholeNumber(CustomFpsBox.Value) <= 0)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "Enter the maximum frame rate for the video, or pick one of the other rates.",
                    "The custom frame rate needs a number");
                CustomFpsBox.Focus();
                return;
            }

            string presetName = null;
            if (SavePresetCheck.IsChecked == true)
            {
                presetName = PresetNameBox.Text?.Trim();
                if (String.IsNullOrEmpty(presetName))
                {
                    await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                        "Give the preset a name, or untick \"Save current settings as new preset\".",
                        "The preset needs a name");
                    PresetNameBox.Focus();
                    return;
                }
            }

            var path = ResolveTypedPath(out var problem);
            if (path == null)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning, problem, "Can't save the video there");
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "This folder doesn't exist:" + Environment.NewLine + directory,
                    "Can't save the video there");
                return;
            }

            // the picker asks this itself (ShowOverwritePrompt), a typed name does not.
            if (File.Exists(path) && !await NiceDialog.ShowYesNoPromptAsync(this, NiceDialogIcon.Warning,
                    Path.GetFileName(path) + " already exists in this folder. Replace it?",
                    "Replace the existing file?"))
                return;

            _result = new RenderRequest
            {
                Crf = SelectedCrf(),
                MaxHeight = SizeActual.IsChecked == true && _initial.MaxHeight >= _project.HeightPx
                    ? _initial.MaxHeight : SelectedMaxHeight(),
                MaxFps = FpsActual.IsChecked == true && _initial.MaxFps > 0 && !RenderFrameRate.IsBelow(_initial.MaxFps, _fpsCeiling)
                    ? _initial.MaxFps : SelectedMaxFps(),
                HardwareEncoder = HardwareCheck.IsChecked == true,
                Container = SelectedContainer(),
                OutputPath = path,
                CopyToClipboard = CopyCheck.IsChecked == true,
                ShowInFolder = RevealCheck.IsChecked == true,
                DeleteSession = DeleteSessionCheck.IsVisible && DeleteSessionCheck.IsChecked == true,
                SaveAsPresetName = presetName,
            };

            Close(_result);
        }

        /// <summary>What the "Save to" box currently names, with the same rules the Render button
        /// applies (<see cref="RenderOutputPath"/>).</summary>
        private string ResolveTypedPath(out string problem) =>
            RenderOutputPath.Resolve(PathBox.Text, _defaultDirectory, SelectedContainer(), out problem);
    }
}
