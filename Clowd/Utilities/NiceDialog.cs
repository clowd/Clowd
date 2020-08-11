using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.Interop;
using Cyotek.Windows.Forms;
using Ookii.Dialogs.Wpf;
using ScreenVersusWpf;

namespace Clowd
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
            return ShowPromptAsync(parent, icon, content, icon.ToString(), promptTxt);
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
            if (await ShowPromptAsync(parent, NiceDialogIcon.Warning, content, category.ToString() + " configuration required", "Open Settings"))
            {
                App.Current.ShowSettings(category);
            }
        }

        public static Task<bool> ShowPromptAsync(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt = "Close")
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
            return ShowPromptBlockingOrAsync(false, parent, icon, content, mainInstruction, trueTxt, falseTxt, settings, memory);
        }

        public static bool ShowPromptBlocking(
            FrameworkElement parent,
            NiceDialogIcon icon,
            string content,
            string mainInstruction,
            string trueTxt,
            string falseTxt = "Close")
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
            return ShowPromptBlockingOrAsync(true, parent, icon, content, mainInstruction, trueTxt, falseTxt, settings, memory).Result;
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
            Expression<Func<TSettings, RememberPromptChoice>> memory)
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
                dialog.WindowTitle = App.ClowdAppName;
                dialog.MainInstruction = mainInstruction;
                dialog.Content = content;
                dialog.MainIcon = (TaskDialogIcon)(int)icon;

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
            dialog.Text = App.ClowdAppName + " - Color Picker";
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

        public static async Task<System.Windows.Forms.DialogResult> ShowAsNiceDialogAsync(this System.Windows.Forms.CommonDialog dialog, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            try
            {
                var taskSource = new TaskCompletionSource<System.Windows.Forms.DialogResult>();
                uint threadId = 0;

                Thread t = new Thread(() =>
                {
                    threadId = Interop.Kernel32.KERNEL32.GetCurrentThreadId();
                    taskSource.SetResult(dialog.ShowDialog(new Win32Window(ownerHandle)));
                });

                t.SetApartmentState(ApartmentState.STA);
                t.Start();

                // basically, since we opened the dialog in a new thread, we can be sure it is one of the only windows in that thread
                // so we enumerate GetThreadWindows until the window handle is created, and then update the window position

                HwndWindow.HwndWindowHook dialogWindowHook = null;

                while (true)
                {
                    if (taskSource.Task.IsCompleted)
                        break;

                    await Task.Delay(20);

                    if (threadId == 0)
                        continue;

                    var children = USER32EX.GetThreadWindows(threadId);

                    if (children.Count > 0)
                    {
                        var dialogHandle = children
                            .Select(s => HwndWindow.FromHandle(s))
                            .FirstOrDefault(w => w.Owner != null && w.Owner.Handle == ownerHandle);

                        if (dialogHandle == null)
                            continue;

                        AutoPositionNativeWindowHandle(dialogHandle, ownerHandle, isFake);

                        if (dialogHandle.CanHookWndProc)
                        {
                            dialogWindowHook = dialogHandle.AddWndProcHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) =>
                            {
                                if (msg == (int)WindowMessage.WM_SIZE)
                                {
                                    // the window has changed size and it was not the user who resized it (see next condition)
                                    AutoPositionNativeWindowHandle(dialogHandle, ownerHandle, isFake);
                                }
                                else if (msg == (int)WindowMessage.WM_SIZING || msg == (int)WindowMessage.WM_SETCURSOR || msg == (int)WindowMessage.WM_MOUSEACTIVATE)
                                {
                                    // dispose of this hook if the user has interacted with the dialog
                                    dialogWindowHook.Dispose();
                                }
                            });
                        }

                        break;
                    }
                }

                var result = await taskSource.Task;

                return result;
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

        public static Task<TaskDialogButton> ShowAsNiceDialogAsync(this TaskDialog dialog, FrameworkElement parent)
        {
            return FakeShowTaskDialogBlockingOrAsync(parent, dialog, false);
        }

        public static TaskDialogButton ShowAsNiceDialog(this TaskDialog dialog, FrameworkElement parent)
        {
            return FakeShowTaskDialogBlockingOrAsync(parent, dialog, true).Result;
        }

        public static async Task<System.Windows.Forms.DialogResult> ShowAsNiceDialogAsync(this System.Windows.Forms.Form form, FrameworkElement parent)
        {
            CaptureOwner(parent, out var ownerWindow, out var ownerHandle, out var isFake);

            try
            {
                var taskSource = new TaskCompletionSource<bool>();

                form.Load += (_, e) =>
                {
                    form.Location = GetDialogPosition(new System.Drawing.Size(form.Width, form.Height), ownerHandle, isFake);
                };
                form.Closed += (s, e) => taskSource.SetResult(true);
                form.Show(new Win32Window(ownerHandle));

                await taskSource.Task;

                return form.DialogResult;
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

        private static void CaptureOwner(FrameworkElement parent, out Window ownerWindow, out IntPtr ownerHandle, out bool isFake)
        {
            if (parent != null && !(parent is Window))
                parent = TemplatedWindow.GetWindow(parent);

            // get, or create owner window.
            if (parent != null && parent is Window window)
            {
                isFake = false;
                ownerWindow = window;
            }
            else
            {
                isFake = true;
                ownerWindow = new Window()
                {
                    ShowActivated = false,
                    Opacity = 0,
                    WindowStyle = System.Windows.WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true,
                    Width = 1,
                    Height = 1
                };
                ownerWindow.Show();
            }

            //ownerWindow = GetRealOrFakeWindow(parent, out isFake);
            ownerHandle = new WindowInteropHelper(ownerWindow).EnsureHandle();

            // disable parent window
            ownerWindow.IsEnabled = false;
            USER32EX.SetNativeEnabled(ownerHandle, false);
        }

        private static void ReleaseOwner(Window ownerWindow, bool isFake)
        {
            var ownerHandle = new WindowInteropHelper(ownerWindow).Handle;
            if (isFake)
            {
                ownerWindow.Close();
            }
            else
            {
                // enable parent window
                USER32EX.SetNativeEnabled(ownerHandle, true);
                ownerWindow.IsEnabled = true;
            }
        }

        private static void AutoPositionNativeWindowHandle(IntPtr childHandle, IntPtr ownerHandle, bool isFake)
        {
            // update dialog positioning
            USER32.GetWindowRect(childHandle, out var childRect);
            Console.WriteLine(((System.Drawing.Rectangle)childRect).ToString());
            var size = new System.Drawing.Size(childRect.right - childRect.left, childRect.bottom - childRect.top);
            var p = GetDialogPosition(size, ownerHandle, isFake);
            USER32.SetWindowPos(childHandle, SWP_HWND.HWND_TOP, p.X, p.Y, size.Width, size.Height, 0);
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
                USER32.GetWindowRect(ownerHandle, out var ownerRect);

                p.X = (ownerRect.left + ownerRect.right - s.Width) / 2;
                if (p.X < screenRect.X)
                    p.X = screenRect.X;
                else if (p.X + s.Width > screenRect.X + screenRect.Width)
                    p.X = screenRect.X + screenRect.Width - s.Width;

                p.Y = (ownerRect.top + ownerRect.bottom - s.Height) / 2;
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
