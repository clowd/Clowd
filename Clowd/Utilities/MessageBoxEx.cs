using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ookii.Dialogs.Wpf;

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

    public static class MessageBoxEx
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

                Show(wnd, dialog);
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

                TaskDialogButton result = Show(wnd, dialog);
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

                TaskDialogButton result = Show(wnd, dialog);
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

        private static TaskDialogButton Show(FrameworkElement wnd, TaskDialog dialog)
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
