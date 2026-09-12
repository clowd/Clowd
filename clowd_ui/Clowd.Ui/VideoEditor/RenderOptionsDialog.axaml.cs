using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.UI.Services;
using Clowd.VideoSDK.Render;

namespace Clowd.UI.VideoEditor
{
    /// <summary>What the render dialog needs to know about the project it is about to render: the
    /// canvas the edit composes into, the output frame rate, and how long the edit runs. All of it
    /// is shown (the footer's "0:42 · 1920×1080 · 60 fps") and the canvas decides which size caps
    /// could shrink the output at all.</summary>
    /// <param name="WidthPx">Output canvas width in pixels.</param>
    /// <param name="HeightPx">Output canvas height in pixels.</param>
    /// <param name="FpsNum">Output frame rate numerator.</param>
    /// <param name="FpsDen">Output frame rate denominator.</param>
    /// <param name="Duration">How long the rendered video will be.</param>
    public readonly record struct RenderProjectInfo(int WidthPx, int HeightPx, int FpsNum, int FpsDen, TimeSpan Duration);

    /// <summary>
    /// The "Render video" dialog behind the Render flyout's "More options…" row: quality (a CRF
    /// slider, with the three presets as shortcuts onto it), an encode-time size cap, whether the
    /// GPU encoder may be used, where the file goes, and what should happen once it is there. It
    /// renders nothing itself — it returns a <see cref="RenderRequest"/> the caller starts, or
    /// null when the user backed out.
    ///
    /// H.264 in MP4, x264 <c>fast</c>, is the only thing the renderer writes today, so there is no
    /// codec, container or speed row: every row here is a choice that actually changes the file.
    /// </summary>
    public partial class RenderOptionsDialog : Window
    {
        /// <summary>MP4 only — the one container the render tool writes.</summary>
        private static readonly FilePickerFileType Mp4FileType = new FilePickerFileType("MP4 video")
        {
            Patterns = new[] { "*.mp4" },
            MimeTypes = new[] { "video/mp4" },
        };

        private readonly RenderProjectInfo _project;
        private readonly string _defaultDirectory;

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

        private RenderOptionsDialog(RenderProjectInfo project, RenderRequest initial, string defaultOutputPath)
        {
            _project = project;

            InitializeComponent();
            Icon = AppStyles.AppIcon;

            _defaultDirectory = RenderOutputPath.DirectoryOf(defaultOutputPath);

            CrfSlider.Minimum = RenderPresets.MinCrf;
            CrfSlider.Maximum = RenderPresets.MaxCrf;

            // a cap that cannot shrink this project would be a lie — 720p on a 640x360 canvas
            // would have to upscale, which a render never does.
            Size1080.IsEnabled = _project.HeightPx > 1080;
            Size720.IsEnabled = _project.HeightPx > 720;

            SelectQuality(initial.Crf);
            SelectSize(initial.MaxHeight);

            PathBox.Text = defaultOutputPath;
            HardwareCheck.IsChecked = initial.HardwareEncoder;
            CopyCheck.IsChecked = initial.CopyToClipboard;
            RevealCheck.IsChecked = initial.ShowInFolder;
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
            foreach (var size in new[] { SizeFull, Size1080, Size720 })
                size.IsCheckedChanged += (_, _) => SyncSizeCaption();

            SyncQualitySegments();
            SyncSizeCaption();

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
        /// </summary>
        public static async Task<RenderRequest> ShowAsync(Window owner, RenderProjectInfo project,
            RenderRequest initial, string defaultOutputPath)
        {
            ArgumentNullException.ThrowIfNull(initial);

#pragma warning disable CS0618 // the private constructor is the intended one
            var dialog = new RenderOptionsDialog(project, initial, defaultOutputPath);
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
        private static string FormatFps(RenderProjectInfo project)
        {
            if (project.FpsNum <= 0 || project.FpsDen <= 0)
                return "0";

            var fps = project.FpsNum / (double)project.FpsDen;
            return Math.Abs(fps - Math.Round(fps)) < 0.001
                ? Math.Round(fps).ToString(CultureInfo.InvariantCulture)
                : fps.ToString("0.##", CultureInfo.InvariantCulture);
        }

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

        /// <summary>Checks the size segment for <paramref name="maxHeight"/>, falling back to Full
        /// when that cap is one this project cannot use (a remembered 720p meeting a 720p project).</summary>
        private void SelectSize(int maxHeight)
        {
            if (maxHeight == 1080 && Size1080.IsEnabled)
                Size1080.IsChecked = true;
            else if (maxHeight == 720 && Size720.IsEnabled)
                Size720.IsChecked = true;
            else
                SizeFull.IsChecked = true;
        }

        private int SelectedCrf() => RenderPresets.ClampCrf((int)Math.Round(CrfSlider.Value));

        private int SelectedMaxHeight() =>
            Size1080.IsChecked == true ? 1080 :
            Size720.IsChecked == true ? 720 : 0;

        /// <summary>The caption beside the size segments: what the encoder will actually be opened
        /// at, straight from the SDK's own rounding (even dimensions, aspect preserved) so the
        /// number here is the number in the file.</summary>
        private void SyncSizeCaption()
        {
            if (_project.WidthPx <= 0 || _project.HeightPx <= 0)
            {
                SizeCaption.Text = "";
                return;
            }

            var (width, height) = RenderJob.CapSize(_project.WidthPx, _project.HeightPx, SelectedMaxHeight());
            SizeCaption.Text = String.Format(CultureInfo.InvariantCulture, "{0}×{1}", width, height);
        }

        private async Task BrowseAsync()
        {
            var current = ResolveTypedPath(out _);
            var directory = RenderOutputPath.DirectoryOf(current) ?? _defaultDirectory;

            var options = new FilePickerSaveOptions
            {
                Title = "Render video",
                SuggestedFileName = String.IsNullOrEmpty(current) ? null : Path.GetFileName(current),
                DefaultExtension = "mp4",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[] { Mp4FileType },
            };

            if (!String.IsNullOrEmpty(directory) && Directory.Exists(directory))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(directory);

            var picked = await StorageProvider.SaveFilePickerAsync(options);
            var path = picked?.TryGetLocalPath();
            if (!String.IsNullOrEmpty(path))
                PathBox.Text = RenderOutputPath.WithExtension(path);
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
                MaxHeight = SelectedMaxHeight(),
                HardwareEncoder = HardwareCheck.IsChecked == true,
                OutputPath = path,
                CopyToClipboard = CopyCheck.IsChecked == true,
                ShowInFolder = RevealCheck.IsChecked == true,
            };

            Close(_result);
        }

        /// <summary>What the "Save to" box currently names, with the same rules the Render button
        /// applies (<see cref="RenderOutputPath"/>).</summary>
        private string ResolveTypedPath(out string problem) =>
            RenderOutputPath.Resolve(PathBox.Text, _defaultDirectory, out problem);
    }
}
