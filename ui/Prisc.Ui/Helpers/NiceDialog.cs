using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Clowd.Config;
using Clowd.UI.Dialogs;
using Clowd.UI.Dialogs.ColorPicker;

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
    /// <para>
    /// Every one is something the user just asked for, so each comes up in front with the keyboard
    /// (<see cref="WindowNativeExtensions.ForceForeground"/>) even when what asked was a global hotkey
    /// or a capture finishing in another process — neither of which leaves this process the right to
    /// take the foreground, and a dialog that only flashes in the taskbar reads as nothing happening.
    /// </para>
    /// <para>
    /// A window dialog is owned by the caller's window when it has one and is otherwise a top level of
    /// its own. A native file picker cannot be: it needs an owner's StorageProvider, so without a
    /// caller's window it gets a <see cref="TransparentDialogHost"/> for the duration.
    /// </para>
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
            var result = await ShowWindowAsync(dialog, TryGetWindowFromVisual(parent), () => dialog.Result);
            return result == true;
        }

        /// <summary>
        /// A yes/no prompt carrying a verification checkbox (the "don't ask again" opt-out). The
        /// returned <c>Verified</c> is the state of that checkbox when the dialog closed; it is
        /// up to the caller to decide whether a cancel should still honour it.
        /// </summary>
        public static async Task<VerifiableResult> ShowVerifiableDialogAsync(
            Visual parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt,
            string verificationTxt)
        {
            var dialog = new MessageDialog(icon, content, mainInstruction, trueTxt, falseTxt, verificationTxt: verificationTxt);
            var result = await ShowWindowAsync(dialog, TryGetWindowFromVisual(parent), () => dialog.Result);
            return new VerifiableResult(result == true, dialog.IsVerificationChecked);
        }

        /// <summary>The outcome of a prompt that carried a verification checkbox.</summary>
        public readonly record struct VerifiableResult(bool Result, bool Verified);

        /// <summary>
        /// A prompt with two ways to say yes and one to back out: the affirmative button (which is
        /// the default-focused one, so it should be the safer of the two), an alternate beside it,
        /// and a cancel. Escape and Cmd+W resolve to <see cref="MessageDialogChoice.Cancel"/>.
        /// </summary>
        public static async Task<MessageDialogChoice> ShowThreeWayPromptAsync(
            Visual parent, NiceDialogIcon icon, string content, string mainInstruction,
            string primaryTxt, string alternateTxt, string cancelTxt = "Cancel")
        {
            var dialog = new MessageDialog(icon, content, mainInstruction, primaryTxt, cancelTxt, altTxt: alternateTxt);
            await ShowWindowAsync(dialog, TryGetWindowFromVisual(parent), () => dialog.Result);
            return dialog.Choice;
        }

        public static async Task<Color> ShowColorPromptAsync(Visual parent, Color initial)
        {
            var dialog = new ColorDialog(initial, true);
            ComeForwardWhenOpened(dialog);
            var result = await dialog.ShowAsync(TryGetWindowFromVisual(parent));

            if (result == true)
                return dialog.SelectedColor;

            return initial;
        }

        public static void ShowColorViewer(Color? initial = null)
        {
            var dialog = new ColorDialog(initial, false);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ComeForwardWhenOpened(dialog);
            dialog.Show();
            dialog.Activate();
        }

        public static async Task<SelectedFont> ShowFontDialogAsync(Visual parent, string family, double size, FontStyle style, FontWeight weight)
        {
            var dialog = new FontDialog(family, size, style, weight);
            var result = await ShowWindowAsync(dialog, TryGetWindowFromVisual(parent), () => dialog.SelectedFont != null);
            return result == true ? dialog.SelectedFont : null;
        }

        /// <summary>Picks a font face alone (no size or style); null when cancelled.</summary>
        public static async Task<string> ShowFontFamilyDialogAsync(Visual parent, string family)
        {
            var dialog = new FontDialog(family, familyOnly: true);
            var result = await ShowWindowAsync(dialog, TryGetWindowFromVisual(parent), () => dialog.SelectedFont != null);
            return result == true ? dialog.SelectedFont.TextFontFamilyName : null;
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
            using var storage = GetOrCreateWindowStorage(parent);
            var provider = storage.Provider;

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
            using var storage = GetOrCreateWindowStorage(parent);
            var provider = storage.Provider;

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
                fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
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

        /// <summary>The visible window <paramref name="parent"/> is in, or null: a window dialog without
        /// one is simply its own top level.</summary>
        private static Window TryGetWindowFromVisual(Visual parent) =>
            parent != null && TopLevel.GetTopLevel(parent) is Window window && window.IsVisible ? window : null;

        /// <summary>
        /// The owner a native file picker opens over, brought to the front first (the picker opens
        /// activated only over a foreground owner): the caller's window, or failing that a
        /// <see cref="TransparentDialogHost"/>, which takes the foreground itself and is closed when
        /// the returned value is disposed.
        /// </summary>
        private static WindowStorage GetOrCreateWindowStorage(Visual parent)
        {
            if (TryGetWindowFromVisual(parent) is { } window)
            {
                WindowNativeExtensions.ForceForeground(window);
                return new WindowStorage(window.StorageProvider, null);
            }

            var host = new TransparentDialogHost();
            host.Show();
            return new WindowStorage(host.StorageProvider, host);
        }

        /// <summary>A picker's storage provider, and the host window to close once the picker is done
        /// (null when the caller's own window owns it).</summary>
        private sealed class WindowStorage : IDisposable
        {
            private readonly Window _host;

            public WindowStorage(IStorageProvider provider, Window host)
            {
                Provider = provider;
                _host = host;
            }

            public IStorageProvider Provider { get; }

            public void Dispose() => _host?.Close();
        }

        /// <summary>Takes the foreground for <paramref name="dialog"/> once it has a handle. A no-op
        /// when it already has it, and a plain SetForegroundWindow when another Clowd window is in
        /// front; keys are only injected when another app is.</summary>
        private static void ComeForwardWhenOpened(Window dialog)
        {
            void OnOpened(object sender, EventArgs e)
            {
                dialog.Opened -= OnOpened;
                WindowNativeExtensions.ForceForeground(dialog);
            }

            dialog.Opened += OnOpened;
        }

        private static Task<bool?> ShowWindowAsync(Window dialog, Window owner, Func<bool?> resultFn)
        {
            ComeForwardWhenOpened(dialog);

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
