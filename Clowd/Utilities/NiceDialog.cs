using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.Interop;
using Cyotek.Windows.Forms;
using Ookii.Dialogs.Wpf;
using ScreenVersusWpf;

namespace Clowd.Utilities
{
    public enum MessageBoxIcon
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
        //public static void ShowNotice(MessageBoxIcon icon, string content)
        //{
        //    ShowNotice(null, icon, content, icon.ToString());
        //}

        public static void ShowNotice(this FrameworkElement wnd, MessageBoxIcon icon, string content)
        {
            ShowNotice(wnd, icon, content, icon.ToString());
        }

        //public static void ShowNotice(MessageBoxIcon icon, string content, string mainInstruction)
        //{
        //    ShowNotice(null, icon, content, mainInstruction);
        //}

        public static void ShowNotice(this FrameworkElement wnd, MessageBoxIcon icon, string content, string mainInstruction)
        {
            using (var dialog = new TaskDialog())
            {
                dialog.WindowTitle = App.ClowdAppName;
                dialog.MainInstruction = mainInstruction;
                dialog.Content = content;
                dialog.MainIcon = (TaskDialogIcon)(int)icon;

                var btn = new TaskDialogButton(ButtonType.Ok);
                dialog.Buttons.Add(btn);

                ShowTaskDialog(wnd, dialog);
            }
        }

        //public static bool ShowPrompt(MessageBoxIcon icon, string content, string promptTxt)
        //{
        //    return ShowPrompt(null, icon, content, icon.ToString(), promptTxt);
        //}

        public static bool ShowPrompt(this FrameworkElement wnd, MessageBoxIcon icon, string content, string promptTxt)
        {
            return ShowPrompt(wnd, icon, content, icon.ToString(), promptTxt);
        }

        //public static bool ShowPrompt(MessageBoxIcon icon, string content, string mainInstruction, string promptTxt)
        //{
        //    return ShowPrompt(null, icon, content, mainInstruction, promptTxt);
        //}

        public static bool ShowPrompt(this FrameworkElement wnd, MessageBoxIcon icon, string content, string mainInstruction, string promptTxt, string cancelTxt = "Close")
        {
            using (var dialog = new TaskDialog())
            {
                dialog.WindowTitle = App.ClowdAppName;
                dialog.MainInstruction = mainInstruction;
                dialog.Content = content;
                dialog.MainIcon = (TaskDialogIcon)(int)icon;

                var trueBtn = new TaskDialogButton(promptTxt);
                var falseBtn = new TaskDialogButton(cancelTxt);

                dialog.Buttons.Add(trueBtn);
                dialog.Buttons.Add(falseBtn);

                TaskDialogButton result = ShowTaskDialog(wnd, dialog);
                return result == trueBtn;
            }
        }

        public static bool ShowPrompt(this FrameworkElement wnd, MessageBoxIcon icon, string content, string mainInstruction, string promptTxt, string cancelTxt, ref RememberPromptChoice rememberSetting)
        {
            if (rememberSetting != RememberPromptChoice.Ask)
            {
                return rememberSetting == RememberPromptChoice.Yes;
            }

            using (var dialog = new TaskDialog())
            {
                dialog.WindowTitle = App.ClowdAppName;
                dialog.MainInstruction = mainInstruction;
                dialog.Content = content;
                dialog.MainIcon = (TaskDialogIcon)(int)icon;

                var trueBtn = new TaskDialogButton(promptTxt);
                var falseBtn = new TaskDialogButton(cancelTxt);

                dialog.Buttons.Add(trueBtn);
                dialog.Buttons.Add(falseBtn);

                dialog.VerificationText = "Don't ask me this again";

                TaskDialogButton result = ShowTaskDialog(wnd, dialog);
                var ret = result == trueBtn;

                if (dialog.IsVerificationChecked)
                {
                    rememberSetting = ret ? RememberPromptChoice.Yes : RememberPromptChoice.No;
                }

                return ret;
            }
        }

        //public static bool ShowYesNoPrompt(MessageBoxIcon icon, string content)
        //{
        //    return ShowPrompt(null, icon, content, icon.ToString(), "Yes", "No");
        //}

        public static bool ShowYesNoPrompt(this FrameworkElement wnd, MessageBoxIcon icon, string content)
        {
            return ShowPrompt(wnd, icon, content, icon.ToString(), "Yes", "No");
        }

        //public static bool ShowYesNoPrompt(MessageBoxIcon icon, string content, string mainInstruction)
        //{
        //    return ShowPrompt(null, icon, content, mainInstruction, "Yes", "No");
        //}

        public static bool ShowYesNoPrompt(this FrameworkElement wnd, MessageBoxIcon icon, string content, string mainInstruction)
        {
            return ShowPrompt(wnd, icon, content, mainInstruction, "Yes", "No");
        }

        //public static void ShowSettingsPrompt(SettingsCategory category, string content)
        //{
        //    ShowSettingsPrompt(null, category, content);
        //}

        public static void ShowSettingsPrompt(this FrameworkElement wnd, SettingsCategory category, string content)
        {
            if (ShowPrompt(wnd, MessageBoxIcon.Warning, content, category.ToString() + " configuration required", "Open Settings"))
            {
                App.Current.ShowSettings(category);
            }
        }

        public static async Task<Color> ShowColorDialog(this FrameworkElement wnd, Color initial)
        {
            bool isFake;
            var window = GetRealOrFakeWindow(wnd, out isFake);

            ColorPickerDialog dialog = new ColorPickerDialog();
            dialog.Text = "Clowd - Color Picker";
            dialog.ShowAlphaChannel = true;
            dialog.StartPosition = isFake ? System.Windows.Forms.FormStartPosition.CenterScreen : System.Windows.Forms.FormStartPosition.CenterParent;
            dialog.Color = System.Drawing.Color.FromArgb(initial.A, initial.R, initial.G, initial.B);

            await FakeShowWinFormAsDialog(window, dialog);

            var result = dialog.DialogResult;

            if (isFake)
                window.Close();

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

        private static async Task FakeShowWinFormAsDialog(Window window, System.Windows.Forms.Form form)
        {
            TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();
            var hWnd = new WindowInteropHelper(window).EnsureHandle();

            // disable parent window
            window.IsEnabled = false;
            USER32EX.SetNativeEnabled(hWnd, false);

            form.Load += (_, e) =>
            {
                if (form.StartPosition == System.Windows.Forms.FormStartPosition.CenterParent)
                {
                    // center to parent
                    var mth = form.GetType().GetMethod("CenterToParent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    mth.Invoke(form, new object[0]);
                }
                else if (form.StartPosition == System.Windows.Forms.FormStartPosition.CenterScreen)
                {
                    // https://referencesource.microsoft.com/#System.Windows.Forms/winforms/Managed/System/WinForms/Form.cs,23f615d34fbe4eb3,references
                    var p = new System.Drawing.Point();
                    var desktop = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition);
                    var screenRect = desktop.WorkingArea;
                    p.X = Math.Max(screenRect.X, screenRect.X + (screenRect.Width - form.Width) / 2);
                    p.Y = Math.Max(screenRect.Y, screenRect.Y + (screenRect.Height - form.Height) / 2);
                    form.Location = p;
                }
            };
            form.Closed += (s, e) => source.SetResult(true);
            form.Show(new Extensions.Wpf32Window(window));

            await source.Task;

            // enable parent window
            USER32EX.SetNativeEnabled(hWnd, true);
            window.IsEnabled = true;
        }

        private static Window GetRealOrFakeWindow(FrameworkElement wnd, out bool isFake)
        {
            if (wnd != null && !(wnd is Window))
                wnd = TemplatedWindow.GetWindow(wnd);

            if (wnd != null && wnd is Window window)
            {
                isFake = false;
                return window;
            }

            var owner = new Window()
            {
                ShowActivated = false,
                Opacity = 0,
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Width = 1,
                Height = 1
            };

            owner.Show();
            isFake = true;
            return owner;
        }

        private static TaskDialogButton ShowTaskDialog(FrameworkElement wnd, TaskDialog dialog)
        {
            TaskDialogButton result;

            if (wnd != null && !(wnd is Window))
                wnd = TemplatedWindow.GetWindow(wnd);

            if (wnd != null && wnd is Window window)
            {
                result = dialog.ShowDialog(window);
            }
            else
            {
                result = dialog.Show();
            }

            return result;
        }
    }
}
