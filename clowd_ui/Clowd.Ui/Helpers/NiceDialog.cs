using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Clowd.Config;
using Clowd.UI.Dialogs;
using Clowd.UI.Dialogs.ColorPicker;
using Clowd.Util;

namespace Clowd.UI.Helpers
{
    public enum NiceDialogIcon
    {
        None = 0,
        Information = ushort.MaxValue - 2, // TD_INFORMATION_ICON
        Warning = ushort.MaxValue, // TD_WARNING_ICON
        Error = ushort.MaxValue - 1, // TD_ERROR_ICON
        Shield = ushort.MaxValue - 3, // TD_SHIELD_ICON
        ShieldBlueBar = ushort.MaxValue - 4,
        ShieldGrayBar = ushort.MaxValue - 8,
        ShieldWarningYellowBar = ushort.MaxValue - 5,
        ShieldErrorRedBar = ushort.MaxValue - 6,
        ShieldSuccessGreenBar = ushort.MaxValue - 7,
    }

    /// <summary>
    /// Cross-platform replacement for the WPF/WinForms dialog helpers. All prompts are backed by
    /// a single <see cref="MessageDialog"/> window, the font prompt by <see cref="FontDialog"/>,
    /// color prompts by the ColorPicker dialogs, and file pickers by Avalonia's StorageProvider
    /// (decision table #49).
    /// </summary>
    public static class NiceDialog
    {
        public static Task ShowNoticeAsync(Visual parent, NiceDialogIcon icon, string content, string mainInstruction = null)
        {
            return ShowDialogAsync(parent, icon, content, mainInstruction ?? icon.ToString());
        }

        public static Task<bool> ShowPromptAsync(Visual parent, NiceDialogIcon icon, string content, string mainInstruction, string promptTxt)
        {
            return ShowDialogAsync(parent, icon, content, mainInstruction, promptTxt, "Close");
        }

        public static Task<bool> ShowYesNoPromptAsync(Visual parent, NiceDialogIcon icon, string content, string mainInstruction = null)
        {
            return ShowDialogAsync(parent, icon, content, mainInstruction ?? icon.ToString(), "Yes", "No");
        }

        public static async Task<bool> ShowDialogAsync(
            Visual parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction = null,
            string trueTxt = "OK",
            string falseTxt = null,
            NiceDialogIcon footerIcon = NiceDialogIcon.None,
            string footerTxt = null)
        {
            var dialog = new MessageDialog(icon, content, mainInstruction, trueTxt, falseTxt, footerIcon, footerTxt);
            var result = await ShowWindowAsync(dialog, GetOwnerWindow(parent), () => dialog.Result);
            return result == true;
        }

        public static async Task<Color> ShowColorPromptAsync(Visual parent, Color initial)
        {
            var dialog = new ColorDialog(HslRgbColor.FromColor(initial), true);
            var result = await dialog.ShowAsync(GetOwnerWindow(parent));

            if (result == true)
                return dialog.CurrentColor.ToColor();

            return initial;
        }

        public static void ShowColorViewer(Color? initial = null)
        {
            HslRgbColor color = null;
            if (initial.HasValue)
                color = HslRgbColor.FromColor(initial.Value);

            var dialog = new ColorDialog(color, false);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            dialog.Activate();
        }

        public static async Task<SelectedFont> ShowFontDialogAsync(Visual parent, string family, double size, FontStyle style, FontWeight weight)
        {
            var dialog = new FontDialog(family, size, style, weight);
            var result = await ShowWindowAsync(dialog, GetOwnerWindow(parent), () => dialog.SelectedFont != null);
            return result == true ? dialog.SelectedFont : null;
        }

        public class SelectedFont
        {
            public string TextFontFamilyName { get; init; }
            public double TextFontSize { get; init; }
            public FontStyle TextFontStyle { get; init; }
            public FontWeight TextFontWeight { get; init; }
        }

        public static async Task<string[]> ShowSelectFilesDialog(Visual parent, string title = null, string initialDirectory = null,
                                                                 bool multiSelect = false, FilePickerFileType[] filter = null,
                                                                 string suggestedFileName = null)
        {
            var provider = GetStorageProvider(parent);
            if (provider == null)
                return null;

            var options = new FilePickerOpenOptions { AllowMultiple = multiSelect };

            if (!String.IsNullOrWhiteSpace(title))
                options.Title = title;

            // pre-fills the name box — how the "locate a moved file" prompt says which file it is
            // after (the shell still lets the user pick anything).
            if (!String.IsNullOrWhiteSpace(suggestedFileName))
                options.SuggestedFileName = suggestedFileName;

            if (filter != null)
                options.FileTypeFilter = filter;

            if (!String.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(initialDirectory);

            var picked = await provider.OpenFilePickerAsync(options);
            var paths = picked?.Select(f => f.TryGetLocalPath()).Where(p => !String.IsNullOrEmpty(p)).ToArray();

            if (paths != null && paths.Length > 0)
                return paths;

            return null;
        }

        public static async Task<string> ShowSaveImageDialog(Visual parent, Bitmap bitmap, string directory, string filePattern)
        {
            var provider = GetStorageProvider(parent);
            if (provider == null)
                return null;

            if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                directory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            filePattern ??= SettingsCapture.DefaultFilenamePattern;
            filePattern = Path.GetFileNameWithoutExtension(filePattern);

            string fileName;
            try
            {
                fileName = PathConstants.GetFreePatternFileName(directory, filePattern);
            }
            catch
            {
                fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            }

            var options = new FilePickerSaveOptions
            {
                Title = "Save Image",
                SuggestedFileName = fileName,
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
                // Avalonia's Bitmap encoder writes PNG only, so PNG is the single offered type
                // (the WPF build offered jpeg/bmp/etc via its own encoders).
                FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
            };

            if (Directory.Exists(directory))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(directory);

            var picked = await provider.SaveFilePickerAsync(options);
            if (picked == null)
                return null; // canceled

            var file = picked.TryGetLocalPath();
            if (String.IsNullOrEmpty(file))
                return null;

            try
            {
                // ShowOverwritePrompt is true so the user will have already been asked if they
                // are happy to overwrite this file.
                bitmap.Save(file, PngBitmapEncoderOptions.Default);
            }
            catch (Exception e)
            {
                await ShowNoticeAsync(parent, NiceDialogIcon.Error, e.Message);
                SentryConfig.CaptureHandled(e, "dialog.file-browser");
                return null;
            }

            return file;
        }

        private static Window GetOwnerWindow(Visual parent)
        {
            if (parent != null && TopLevel.GetTopLevel(parent) is Window owner && owner.IsVisible)
                return owner;

            // No (visible) parent — fall back to any visible app window, preferring the active
            // one. When nothing is available the dialog is simply shown non-modal, centered on
            // screen (replaces the WPF "fake owner window" mechanism).
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Exclude Avalonia-internal transient top-levels (e.g. TrayPopupRoot, the window
                // that hosts the tray context menu). When a dialog is opened from a tray menu item,
                // that popup is still listed and reports IsVisible/IsActive, but it is torn down as
                // the menu dismisses — picking it as the dialog owner makes ShowDialog's CenterOwner
                // placement throw "Windowing backend wasn't properly initialized." once its backend
                // is gone. Our own windows all live under Clowd.* namespaces.
                static bool IsAppWindow(Window w) => !w.GetType().FullName!.StartsWith("Avalonia", StringComparison.Ordinal);

                return desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible && IsAppWindow(w))
                       ?? desktop.Windows.FirstOrDefault(w => w.IsVisible && IsAppWindow(w));
            }

            return null;
        }

        private static IStorageProvider GetStorageProvider(Visual parent)
        {
            if (parent != null && TopLevel.GetTopLevel(parent) is { } topLevel)
                return topLevel.StorageProvider;

            return GetOwnerWindow(null)?.StorageProvider;
        }

        private static Task<bool?> ShowWindowAsync(Window dialog, Window owner, Func<bool?> resultFn)
        {
            if (owner != null)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return dialog.ShowDialog<bool?>(owner);
            }

            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var tcs = new TaskCompletionSource<bool?>();
            dialog.Closed += (_, _) => tcs.TrySetResult(resultFn());
            dialog.Show();
            dialog.Activate();
            return tcs.Task;
        }
    }
}
