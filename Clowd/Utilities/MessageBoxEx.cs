using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ookii.Dialogs.Wpf;

namespace Clowd.Utilities
{
    public class MessageBoxEx
    {
        public static bool ShowAction(
            Window wnd,
            string instruction,
            string content,
            TaskDialogIcon icon,
            string trueBtnTxt,
            string falseBtnTxt = "Close")
        {
            using (var dialog = new TaskDialog())
            {
                dialog.WindowTitle = App.ClowdAppName;
                dialog.MainInstruction = instruction;
                dialog.Content = content;
                dialog.MainIcon = icon;

                var trueBtn = new TaskDialogButton(trueBtnTxt);
                var falseBtn = new TaskDialogButton(falseBtnTxt);

                dialog.Buttons.Add(trueBtn);
                dialog.Buttons.Add(falseBtn);

                TaskDialogButton result;

                if (wnd != null)
                {
                    result = dialog.ShowDialog(wnd);
                }
                else
                {
                    result = dialog.Show();
                }

                return result == trueBtn;
            }
        }
    }
}
