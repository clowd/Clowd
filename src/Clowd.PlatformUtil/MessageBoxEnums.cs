using System;
using System.Collections.Generic;
using System.Text;

namespace Clowd.PlatformUtil
{
    public enum MessageBoxResult
    {
        None = 0,
        OK = 1,
        Cancel = 2,
        Yes = 6,
        No = 7,
    }

    public enum MessageBoxImage
    {
        None = 0,
        Hand = 0x00000010,
        Question = 0x00000020,
        Exclamation = 0x00000030,
        Asterisk = 0x00000040,
        Stop = Hand,
        Error = Hand,
        Warning = Exclamation,
        Information = Asterisk,
    }

    public enum MessageBoxButton
    {
        OK = 0x00000000,
        OKCancel = 0x00000001,
        YesNoCancel = 0x00000003,
        YesNo = 0x00000004,
    }
}
