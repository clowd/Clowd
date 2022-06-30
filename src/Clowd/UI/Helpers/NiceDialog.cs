using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.PlatformUtil;
using Clowd.PlatformUtil.Windows;

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

    public static class NiceDialog
    {
        public static Task ShowNoticeAsync(FrameworkElement parent, NiceDialogIcon icon, string content)
        {
            return ShowDialogAsync(parent, icon, content, icon.ToString());
        }

        public static Task ShowNoticeAsync(FrameworkElement parent, NiceDialogIcon icon, string content, string mainInstruction)
        {
            return ShowDialogAsync(parent, icon, content, mainInstruction);
        }

        public static Task<bool> ShowPromptAsync(FrameworkElement parent, NiceDialogIcon icon, string content, string promptTxt)
        {
            return ShowDialogAsync(parent, icon, content, icon.ToString(), promptTxt, "Close");
        }

        public static Task<bool> ShowYesNoPromptAsync(FrameworkElement parent, NiceDialogIcon icon, string content)
        {
            return ShowYesNoPromptAsync(parent, icon, content, icon.ToString());
        }

        public static Task<bool> ShowYesNoPromptAsync(FrameworkElement parent, NiceDialogIcon icon, string content, string mainInstruction)
        {
            return ShowDialogAsync(parent, icon, content, mainInstruction, "Yes", "No");
        }

        public static async Task ShowSettingsPromptAsync(FrameworkElement parent, SettingsPageTab category, string content)
        {
            if (await ShowDialogAsync(parent, NiceDialogIcon.Warning, content, "Settings configuration required", "Open Settings", "Close"))
            {
                PageManager.Current.GetSettingsPage().Open(category);
            }
        }

        public static async Task<bool> ShowDialogAsync(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction = null,
            string trueTxt = "OK",
            string falseTxt = null,
            NiceDialogIcon footerIcon = NiceDialogIcon.None,
            string footerTxt = null)
        {
            var page = new TaskDialogPage()
            {
                Heading = mainInstruction,
                Text = content,
                Caption = Constants.ClowdAppName,
                Icon = GetTaskIconFromNiceIcon(icon),
                SizeToContent = false,
                AllowMinimize = false,
                AllowCancel = true,
            };

            if (!String.IsNullOrWhiteSpace(footerTxt))
            {
                page.Footnote = new TaskDialogFootnote()
                {
                    Icon = GetTaskIconFromNiceIcon(footerIcon),
                    Text = footerTxt,
                };
            }

            var btnTrue = new TaskDialogButton(trueTxt);
            page.Buttons.Add(btnTrue);

            if (!String.IsNullOrWhiteSpace(falseTxt))
            {
                page.Buttons.Add(new TaskDialogButton(falseTxt));
            }

            var result = await page.ShowAsNiceDialogAsync(parent);
            return result == btnTrue;
        }

        private static TaskDialogIcon GetTaskIconFromNiceIcon(NiceDialogIcon nice)
        {
            return nice switch
            {
                NiceDialogIcon.None => TaskDialogIcon.None,
                NiceDialogIcon.Information => TaskDialogIcon.Information,
                NiceDialogIcon.Warning => TaskDialogIcon.Warning,
                NiceDialogIcon.Error => TaskDialogIcon.Error,
                NiceDialogIcon.Shield => TaskDialogIcon.Shield,
                NiceDialogIcon.ShieldBlueBar => TaskDialogIcon.ShieldBlueBar,
                NiceDialogIcon.ShieldGrayBar => TaskDialogIcon.ShieldGrayBar,
                NiceDialogIcon.ShieldWarningYellowBar => TaskDialogIcon.ShieldWarningYellowBar,
                NiceDialogIcon.ShieldErrorRedBar => TaskDialogIcon.ShieldErrorRedBar,
                NiceDialogIcon.ShieldSuccessGreenBar => TaskDialogIcon.ShieldSuccessGreenBar,
                _ => throw new ArgumentOutOfRangeException(nameof(nice), nice, null)
            };
        }

        public static async Task<Color> ShowColorPromptAsync(FrameworkElement parent, Color initial)
        {
            var clr = new Dialogs.ColorPicker.ColorDialog(initial, true);
            await clr.ShowAsNiceDialogAsync(parent);

            if (clr.MyDialogResult == true)
            {
                return clr.CurrentColor;
            }
            else
            {
                return initial;
            }
        }

        public static void ShowColorViewer(Color? initial = null)
        {
            var clr = new Dialogs.ColorPicker.ColorDialog(initial, false);
            clr.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            clr.Show();
            clr.GetPlatformWindow().Activate();
        }

        public static async Task<SelectedFont> ShowFontDialogAsync(
            FrameworkElement parent,
            string fFamily,
            double fSize,
            System.Windows.FontStyle fStyle,
            System.Windows.FontWeight fWeight)
        {
            using var dlg = new ExFontDialog();
            float wfSize = (float)fSize / 96 * 72;

            System.Drawing.FontStyle wfStyle;
            if (fStyle == FontStyles.Italic)
                wfStyle = System.Drawing.FontStyle.Italic;
            else
                wfStyle = System.Drawing.FontStyle.Regular;

            if (fWeight.ToOpenTypeWeight() > 400)
                wfStyle |= System.Drawing.FontStyle.Bold;

            dlg.Font = new System.Drawing.Font(fFamily, wfSize, wfStyle, System.Drawing.GraphicsUnit.Point);
            dlg.FontMustExist = true;
            dlg.MaxSize = 64;
            dlg.MinSize = 8;
            dlg.ShowColor = false;
            dlg.ShowEffects = false;
            dlg.ShowHelp = false;
            dlg.AllowVerticalFonts = false;
            dlg.AllowVectorFonts = true;
            dlg.AllowScriptChange = false;

            if (await dlg.ShowAsNiceDialogAsync(parent))
            {
                return new SelectedFont()
                {
                    TextFontFamilyName = dlg.Font.FontFamily.GetName(0),
                    TextFontSize = dlg.Font.SizeInPoints / 72 * 96,
                    TextFontStyle = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Italic) ? FontStyles.Italic : FontStyles.Normal,
                    TextFontWeight = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Bold) ? FontWeights.Bold : FontWeights.Normal,
                };
            }

            return null;
        }

        public class SelectedFont
        {
            public string TextFontFamilyName { get; init; }
            public double TextFontSize { get; init; }
            public FontStyle TextFontStyle { get; init; }
            public FontWeight TextFontWeight { get; init; }
        }

        public static async Task<string[]> ShowSelectFilesDialog(FrameworkElement parent, string title = null, string initialDirectory = null, bool multiSelect = false, string filter = null)
        {
            using var dialog = new OpenFileDialog();

            if (!String.IsNullOrWhiteSpace(title))
                dialog.Title = title;

            if (!String.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                dialog.InitialDirectory = initialDirectory;

            dialog.Multiselect = multiSelect;

            if (!String.IsNullOrWhiteSpace(filter))
                dialog.Filter = filter;

            if (await dialog.ShowAsNiceDialogAsync(parent) && dialog.FileNames.Any())
                return dialog.FileNames;

            return null;
        }

        public static async Task<string> ShowSelectSaveFileDialog(FrameworkElement parent, string title, string directory, string defaultName, string extension)
        {
            directory ??= Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            extension = "." + extension.Trim('.').Trim();

            // generate unique file name (screenshot-1.png, screenshot-2.png etc)
            string initialName = $"{defaultName}{extension}";
            if (File.Exists(Path.Combine(directory, initialName)))
            {
                int i = 1;
                do
                {
                    initialName = $"{defaultName}-{i}{extension}";
                    i++;
                } while (File.Exists(Path.Combine(directory, initialName)));
            }

            using var dlg = new SaveFileDialog();

            dlg.Title = title;
            dlg.FileName = initialName; // Default file name
            dlg.DefaultExt = extension; // Default file extension
            dlg.Filter = $"{defaultName}|*{extension}"; // Filter files by extension
            dlg.OverwritePrompt = true;
            dlg.InitialDirectory = directory;

            // Show save file dialog box
            Nullable<bool> result = await dlg.ShowAsNiceDialogAsync(parent);

            if (result == true)
            {
                var file = dlg.FileName;
                // OverwritePrompt is true so the user will have already been asked if they are happy to overwrite this file
                if (File.Exists(file))
                    File.Delete(file);
                return file;
            }

            return null;
        }

        public static async Task<bool> ShowAsNiceDialogAsync(this CommonDialog dialog, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);
            try
            {
                var tcs = new TaskCompletionSource<bool>();
                var thread = new Thread(() =>
                {
                    try
                    {
                        if (dialog is IExCommonDialog ex)
                        {
                            ex.Created += (s, ev) =>
                            {
                                AutoPositionNativeWindowHandle(ev.Handle, ownerHandle, isFake);
                            };
                        }

                        // it might be possible to use a NativeWindow here to grab the hwnd of the dialog.
                        // https://www.codeproject.com/Articles/16276/Customizing-OpenFileDialog-in-NET
                        // basically wait for WM_SHOWWINDOW

                        var result = dialog.ShowDialog(new Win32Window(ownerHandle));
                        tcs.SetResult(result == DialogResult.OK || result == DialogResult.Yes);
                    }
                    catch (Exception e)
                    {
                        tcs.SetException(e);
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return await tcs.Task;
            }
            finally
            {
                ReleaseOwner(ownerWindow, isFake);
            }
        }

        public static Task<bool?> ShowAsNiceDialogAsync(this Window wnd, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            var taskSource = new TaskCompletionSource<bool?>();

            WindowInteropHelper helper = new WindowInteropHelper(wnd);
            helper.EnsureHandle();
            var pwnd = User32Window.FromHandle(helper.Handle);

            wnd.Loaded += (s, e) =>
            {
                AutoPositionNativeWindowHandle(helper.Handle, ownerHandle, isFake);
            };

            wnd.Closed += (s, e) =>
            {
                ReleaseOwner(ownerWindow, isFake);
                taskSource.SetResult((wnd as IWpfNiceDialog)?.MyDialogResult);
            };

            wnd.Owner = ownerWindow;
            wnd.Show();
            pwnd.Activate();

            return taskSource.Task;
        }

        public static Task<DialogResult> ShowAsNiceDialogAsync(this Form form, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            var taskSource = new TaskCompletionSource<DialogResult>();

            form.Load += (_, e) =>
            {
                form.Location = GetDialogPosition(new System.Drawing.Size(form.Width, form.Height), ownerHandle, isFake);
            };

            form.Closed += (s, e) =>
            {
                ReleaseOwner(ownerWindow, isFake);
                taskSource.SetResult(form.DialogResult);
            };

            form.Show(new Win32Window(ownerHandle));

            var pOwner = Platform.Current.GetWindowFromHandle(form.Handle);
            pOwner.Activate();

            return taskSource.Task;
        }

        public static async Task<TaskDialogButton> ShowAsNiceDialogAsync(this TaskDialogPage dialog, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            try
            {
                TaskDialogStartupLocation location = isFake ? TaskDialogStartupLocation.CenterScreen : TaskDialogStartupLocation.CenterOwner;

                if (String.IsNullOrWhiteSpace(dialog.Caption))
                    dialog.Caption = Constants.ClowdAppName;

                dialog.AllowMinimize = false;
                dialog.AllowCancel = true;
                return await Task.Run(() => TaskDialog.ShowDialog(ownerHandle, dialog, location));
            }
            finally
            {
                ReleaseOwner(ownerWindow, isFake);
            }
        }

        public static Window ShowNewFakeOwnerWindow(bool showInTaskbar = true)
        {
            var ownerWindow = new Window()
            {
                ShowActivated = false,
                Opacity = 0,
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                ShowInTaskbar = showInTaskbar,
                Width = 1,
                Height = 1,
            };
            ownerWindow.Show();
            return ownerWindow;
        }

        private static void CaptureOwner(FrameworkElement parent, out Window ownerWindow, out IntPtr ownerHandle, out bool isFake)
        {
            if (parent != null && !(parent is Window))
                parent = Window.GetWindow(parent);

            // get, or create owner window.
            if (parent != null && parent is Window window)
            {
                isFake = false;
                ownerWindow = window;
            }
            else
            {
                isFake = true;
                ownerWindow = ShowNewFakeOwnerWindow();
            }

            try
            {
                ownerHandle = new WindowInteropHelper(ownerWindow).EnsureHandle();
                var pOwner = Platform.Current.GetWindowFromHandle(ownerHandle);

                // disable parent window
                ownerWindow.IsEnabled = false;
                pOwner.SetEnabled(false);
            }
            catch when (!isFake)
            {
                // if we tried to capture a real window and it failed, the window is probably closed.. so lets just create a fake one
                CaptureOwner(null, out ownerWindow, out ownerHandle, out isFake);
            }
        }

        private static void ReleaseOwner(Window ownerWindow, bool isFake)
        {
            var ownerHandle = new WindowInteropHelper(ownerWindow).Handle;
            var pOwner = Platform.Current.GetWindowFromHandle(ownerHandle);
            if (isFake)
            {
                ownerWindow.Close();
            }
            else
            {
                // enable parent window
                pOwner.SetEnabled(true);
                ownerWindow.IsEnabled = true;
                pOwner.Activate();
            }
        }

        private static System.Drawing.Point GetDialogPosition(System.Drawing.Size dialogSize, IntPtr ownerHandle, bool ownerIsFake)
        {
            if (ownerIsFake)
            {
                // center to screen containing mouse cursor
                // https://referencesource.microsoft.com/#System.Windows.Forms/winforms/Managed/System/WinForms/Form.cs,23f615d34fbe4eb3,references

                var p = new System.Drawing.Point();
                var desktop = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition);
                var screenRect = desktop.WorkingArea;
                p.X = Math.Max(screenRect.X, screenRect.X + (screenRect.Width - dialogSize.Width) / 2);
                p.Y = Math.Max(screenRect.Y, screenRect.Y + (screenRect.Height - dialogSize.Height) / 2);

                return p;
            }
            else
            {
                // center to parent window
                // https://referencesource.microsoft.com/#System.Windows.Forms/winforms/Managed/System/WinForms/Form.cs,2f16fb935e799fac

                var p = new System.Drawing.Point();
                var s = dialogSize;
                var desktop = System.Windows.Forms.Screen.FromHandle(ownerHandle);
                var screenRect = desktop.WorkingArea;
                //USER32.GetWindowRect(ownerHandle, out var ownerRect);
                var pOwner = Platform.Current.GetWindowFromHandle(ownerHandle);
                var ownerRect = pOwner.WindowBounds;

                p.X = (ownerRect.Left + ownerRect.Right - s.Width) / 2;
                if (p.X < screenRect.X)
                    p.X = screenRect.X;
                else if (p.X + s.Width > screenRect.X + screenRect.Width)
                    p.X = screenRect.X + screenRect.Width - s.Width;

                p.Y = (ownerRect.Top + ownerRect.Bottom - s.Height) / 2;
                if (p.Y < screenRect.Y)
                    p.Y = screenRect.Y;
                else if (p.Y + s.Height > screenRect.Y + screenRect.Height)
                    p.Y = screenRect.Y + screenRect.Height - s.Height;

                return p;
            }
        }

        private static void AutoPositionNativeWindowHandle(IntPtr childHandle, IntPtr ownerHandle, bool isFake)
        {
            // update dialog positioning
            var pChild = Platform.Current.GetWindowFromHandle(childHandle);
            var size = pChild.WindowBounds.Size;
            var p = (ScreenPoint)GetDialogPosition((System.Drawing.Size)size, ownerHandle, isFake);
            pChild.SetPosition(new ScreenRect(p, size));
            pChild.Activate();
        }

        private interface IExCommonDialog
        {
            event EventHandler<HwndCreatedEventArgs> Created;
        }

        private class ExFontDialog : FontDialog, IExCommonDialog
        {
            public event EventHandler<HwndCreatedEventArgs> Created;

            protected override IntPtr HookProc(IntPtr hWnd, int msg, IntPtr wparam, IntPtr lparam)
            {
                var result = base.HookProc(hWnd, msg, wparam, lparam);
                if (msg == 0x0110 /* INITDIALOG */) Created?.Invoke(this, new HwndCreatedEventArgs(hWnd));
                return result;
            }
        }

        private class HwndCreatedEventArgs : EventArgs
        {
            public IntPtr Handle { get; }

            public HwndCreatedEventArgs(IntPtr handle)
            {
                Handle = handle;
            }
        }

        private class Win32Window : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }

            public Win32Window(IntPtr handle)
            {
                Handle = handle;
            }
        }
    }

    public interface IWpfNiceDialog
    {
        bool? MyDialogResult { get; }
    }
}
