using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.Util;
using Cyotek.Windows.Forms;
using Ookii.Dialogs.Wpf;

namespace Clowd.UI.Helpers
{
    public enum NiceDialogIcon
    {
        Shield = 65532,
        Information = 65533,
        Error = 65534,
        Warning = 65535
    }

    public enum RememberPromptChoice
    {
        Ask = 0,
        Yes = 1,
        No = 2,
    }

    public static class NiceDialog
    {
        public static Task ShowNoticeAsync(FrameworkElement parent, NiceDialogIcon icon, string content)
        {
            return ShowPromptAsync(parent, icon, content, icon.ToString(), "Ok", null);
        }

        public static Task ShowNoticeAsync(FrameworkElement parent, NiceDialogIcon icon, string content, string mainInstruction)
        {
            return ShowPromptAsync(parent, icon, content, mainInstruction, "Ok", null);
        }

        public static Task<bool> ShowPromptAsync(FrameworkElement parent, NiceDialogIcon icon, string content, string promptTxt)
        {
            return ShowPromptAsync(parent, icon, content, icon.ToString(), promptTxt, "Close");
        }

        public static Task<bool> ShowYesNoPromptAsync(FrameworkElement parent, NiceDialogIcon icon, string content)
        {
            return ShowPromptAsync(parent, icon, content, icon.ToString(), "Yes", "No");
        }

        public static Task<bool> ShowYesNoPromptAsync(FrameworkElement parent, NiceDialogIcon icon, string content, string mainInstruction)
        {
            return ShowPromptAsync(parent, icon, content, mainInstruction, "Yes", "No");
        }

        public static async Task ShowSettingsPromptAsync(FrameworkElement parent, SettingsCategory category, string content)
        {
            if (await ShowPromptAsync(parent, NiceDialogIcon.Warning, content, category.ToString() + " configuration required", "Open Settings", "Close"))
            {
                //pages.CreateSettingsPage().Open(category);
            }
        }

        public static Task<bool> ShowPromptAsync(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt)
        {
            return ShowPromptAsync<object>(parent, icon, content, mainInstruction, trueTxt, falseTxt, null, null);
        }

        public static Task<bool> ShowPromptAsync<TSettings>(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt,
            TSettings settings,
            Expression<Func<TSettings, RememberPromptChoice>> memory)
            where TSettings : class
        {
            return ShowPromptBlockingOrAsync(false, parent, icon, content, mainInstruction, trueTxt, falseTxt, settings, memory, 0, null);
        }

        public static Task<bool> ShowPromptAsync(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt,
            NiceDialogIcon footerIcon,
            string footerTxt)
        {
            return ShowPromptBlockingOrAsync<object>(false, parent, icon, content, mainInstruction, trueTxt, falseTxt, null, null, footerIcon, footerTxt);
        }

        public static bool ShowPromptBlocking(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt)
        {
            return ShowPromptBlocking<object>(parent, icon, content, mainInstruction, trueTxt, falseTxt, null, null);
        }

        public static bool ShowPromptBlocking<TSettings>(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt,
            TSettings settings,
            Expression<Func<TSettings, RememberPromptChoice>> memory)
            where TSettings : class
        {
            return ShowPromptBlockingOrAsync(true, parent, icon, content, mainInstruction, trueTxt, falseTxt, settings, memory, 0, null).Result;
        }

        public static bool ShowPromptBlocking(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt,
            NiceDialogIcon footerIcon,
            string footerTxt)
        {
            return ShowPromptBlockingOrAsync<object>(true, parent, icon, content, mainInstruction, trueTxt, falseTxt, null, null, footerIcon, footerTxt).Result;
        }

        private static async Task<bool> ShowPromptBlockingOrAsync<TSettings>(
            bool blocking,
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt,
            TSettings settings,
            Expression<Func<TSettings, RememberPromptChoice>> memory,
            NiceDialogIcon footerIcon,
            string footerTxt)
            where TSettings : class
        {
            var hasMemoryStorage = settings != null && memory != null;
            PropertyInfo propInfo = null;

            if (hasMemoryStorage)
            {
                propInfo = GetPropertyInfo(settings, memory);
                var currentMemory = (RememberPromptChoice)propInfo.GetValue(settings);
                if (currentMemory != RememberPromptChoice.Ask)
                    return currentMemory == RememberPromptChoice.Yes;
            }

            using (var dialog = new TaskDialog())
            {
                dialog.WindowTitle = Constants.ClowdAppName;
                dialog.MainInstruction = mainInstruction;
                dialog.Content = content;
                dialog.MainIcon = (TaskDialogIcon)(int)icon;

                if (!String.IsNullOrWhiteSpace(footerTxt))
                {
                    dialog.FooterIcon = (TaskDialogIcon)(int)footerIcon;
                    dialog.Footer = footerTxt;
                }

                var trueBtn = new TaskDialogButton(trueTxt);
                dialog.Buttons.Add(trueBtn);

                if (!String.IsNullOrWhiteSpace(falseTxt))
                {
                    var falseBtn = new TaskDialogButton(falseTxt);
                    dialog.Buttons.Add(falseBtn);
                }

                if (hasMemoryStorage)
                    dialog.VerificationText = "Don't ask me this again";

                var result = await FakeShowTaskDialogBlockingOrAsync(parent, dialog, blocking);
                var isResultTrue = result == trueBtn;

                // if "don't ask" was checked, save this choice
                if (hasMemoryStorage && dialog.IsVerificationChecked)
                    propInfo.SetValue(settings, isResultTrue ? RememberPromptChoice.Yes : RememberPromptChoice.No);

                return isResultTrue;
            }
        }

        public static async Task<Color> ShowColorDialogAsync(FrameworkElement parent, Color initial)
        {
            ColorPickerDialog dialog = new ColorPickerDialog();
            dialog.Text = Constants.ClowdAppName + " - Color Picker";
            dialog.ShowAlphaChannel = true;
            dialog.Color = System.Drawing.Color.FromArgb(initial.A, initial.R, initial.G, initial.B);

            var result = await dialog.ShowAsNiceDialogAsync(parent);

            if (result == System.Windows.Forms.DialogResult.OK)
            {
                var final = dialog.Color;
                return Color.FromArgb(final.A, final.R, final.G, final.B);
            }
            else
            {
                return initial;
            }
        }

        public static async Task<string[]> ShowSelectFilesDialog(FrameworkElement parent, string title = null, string initialDirectory = null, bool multiSelect = false, string filter = null)
        {
            var dialog = new System.Windows.Forms.OpenFileDialog();

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
            directory = directory ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
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
                }
                while (File.Exists(Path.Combine(directory, initialName)));
            }

            var dlg = new System.Windows.Forms.SaveFileDialog();

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

        [Obsolete("You should prefer the System.Windows.Forms variant of CommonDialog's as those do not require reflection to be shown nicely")]
        public static async Task<bool> ShowAsNiceDialogAsync(this Microsoft.Win32.CommonDialog dialog, FrameworkElement parent)
        {
            try
            {
                return await FakeShowCommonDialogAsync((owner) =>
                {
                    var dyn = Exposed.From(dialog);
                    return dyn.RunDialog(owner);
                }, parent);
            }
            catch (Exception e)
            {
                // only viable fallback for Microsoft.Win32.CommonDialog is synchronous execution.
                // dialog.ShowDialog() checks that it is being called on the same thread that created the class
                // therefore, even creating a new WPF window in the new thread won't overcome this issue.
                // using reflection there are a few other async options (such as updating the thread reference)
                // but none are as robust as the above so they are not included.

                CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);
                var result = dialog.ShowDialog(ownerWindow);
                ReleaseOwner(ownerWindow, isFake);
                return result == true;
            }
        }

        public static Task<bool> ShowAsNiceDialogAsync(this System.Windows.Forms.CommonDialog dialog, FrameworkElement parent)
        {
            return FakeShowCommonDialogAsync((owner) =>
            {
                // thank fuck someone with brains wrote the System.Windows.Forms.CommonDialog and the IWin32Window interface.
                var result = dialog.ShowDialog(new Win32Window(owner));
                return result == System.Windows.Forms.DialogResult.OK || result == System.Windows.Forms.DialogResult.Yes;
            }, parent);
        }

        private static Task<bool> FakeShowCommonDialogAsync(Func<IntPtr, bool> showDialog, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);
            //HwndWindow.HwndWindowHook dialogWindowHook = null;
          
            try
            {
                return Task.FromResult(showDialog(ownerHandle));
                //var taskSource = new TaskCompletionSource<bool>();
                //uint threadId = 0;

                //Thread t = new Thread(() =>
                //{
                //    try
                //    {
                //        threadId = Interop.Kernel32.KERNEL32.GetCurrentThreadId();
                //        taskSource.SetResult(showDialog(ownerHandle));
                //    }
                //    catch (Exception e)
                //    {
                //        taskSource.SetException(e);
                //    }
                //});

                //t.SetApartmentState(ApartmentState.STA);
                //t.Start();

                //// basically, since we opened the dialog in a new thread, we can be sure it is one of the only windows in that thread
                //// so we enumerate GetThreadWindows until the window handle is created, and then update the window position

                //while (true)
                //{
                //    if (taskSource.Task.IsCompleted)
                //        break;

                //    await Task.Delay(20);

                //    if (threadId == 0)
                //        continue;

                //    var children = USER32EX.GetThreadWindows(threadId);

                //    if (children.Count > 0)
                //    {
                //        // there are some other "COM" windows that get created in the thread, so we should look for the window that is a child of our ownerWindow or patchWindow.
                //        var dialogHandle = children
                //            .Select(s => HwndWindow.FromHandle(s))
                //            .FirstOrDefault(w => w.Owner != null && w.Owner.Handle == ownerHandle);

                //        if (dialogHandle == null)
                //            continue;

                //        // first pass positioning this dialog. these dialogs like to resize themselves a bunch during rendering, so we will install a hook if we can.
                //        // the hook will detect if the size has changed and will update the window position again. the hook will abort when the user has interacted with the dialog.
                //        AutoPositionNativeWindowHandle(dialogHandle, ownerHandle, isFake);

                //        if (dialogHandle.CanHookWndProc)
                //        {
                //            dialogWindowHook = dialogHandle.AddWndProcHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) =>
                //            {
                //                if (msg == (int)WindowMessage.WM_SIZE)
                //                {
                //                    // the window has changed size and it was not the user who resized it (see next condition)
                //                    AutoPositionNativeWindowHandle(dialogHandle, ownerHandle, isFake);
                //                }
                //                else if (msg == (int)WindowMessage.WM_SIZING || msg == (int)WindowMessage.WM_SETCURSOR || msg == (int)WindowMessage.WM_MOUSEACTIVATE)
                //                {
                //                    // dispose of this hook if the user has interacted with the dialog
                //                    dialogWindowHook.Dispose();
                //                }
                //            });
                //        }

                //        break;
                //    }
                //}

                //return await taskSource.Task;
            }
            catch
            {
                throw;
            }
            finally
            {
                //if (dialogWindowHook != null)
                //    dialogWindowHook.Dispose();

                ReleaseOwner(ownerWindow, isFake);
            }
        }

        public static Task<TaskDialogButton> ShowAsNiceDialogAsync(this TaskDialog dialog, FrameworkElement parent)
        {
            return FakeShowTaskDialogBlockingOrAsync(parent, dialog, false);
        }

        public static TaskDialogButton ShowAsNiceDialog(this TaskDialog dialog, FrameworkElement parent)
        {
            return FakeShowTaskDialogBlockingOrAsync(parent, dialog, true).Result;
        }

        public static Task<System.Windows.Forms.DialogResult> ShowAsNiceDialogAsync(this System.Windows.Forms.Form form, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            var taskSource = new TaskCompletionSource<System.Windows.Forms.DialogResult>();

            form.Load += (_, e) =>
            {
                form.Location = GetDialogPosition(new System.Drawing.Size(form.Width, form.Height), ownerHandle, isFake);
            };
            form.FormClosing += (s, e) => ReleaseOwner(ownerWindow, isFake);
            form.Closed += (s, e) => taskSource.SetResult(form.DialogResult);
            form.Show(new Win32Window(ownerHandle));

            var pOwner = Platform.Current.GetWindowFromHandle(form.Handle);
            pOwner.Activate();

            return taskSource.Task;
        }

        private static async Task<TaskDialogButton> FakeShowTaskDialogBlockingOrAsync(FrameworkElement parent, TaskDialog dialog, bool blocking)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            try
            {
                dialog.Created += (s, e) =>
                {
                    AutoPositionNativeWindowHandle(dialog.Handle, ownerHandle, isFake);
                };

                if (blocking)
                {
                    return dialog.ShowDialog(ownerHandle);
                }
                else
                {
                    return await Task.Run(() => dialog.ShowDialog(ownerHandle));
                }
            }
            catch
            {
                throw;
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

        private static PropertyInfo GetPropertyInfo<TSource, TProperty>(TSource source, Expression<Func<TSource, TProperty>> propertyLambda)
        {
            Type type = typeof(TSource);

            MemberExpression member = propertyLambda.Body as MemberExpression;
            if (member == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a method, not a property.",
                    propertyLambda.ToString()));

            PropertyInfo propInfo = member.Member as PropertyInfo;
            if (propInfo == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a field, not a property.",
                    propertyLambda.ToString()));

            if (type != propInfo.ReflectedType &&
                !type.IsSubclassOf(propInfo.ReflectedType))
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a property that is not from type {1}.",
                    propertyLambda.ToString(),
                    type));

            return propInfo;
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
}
