﻿using Clowd.PlatformUtil;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public enum SettingsCategory
    {
        General,
        Hotkeys,
        Capture,
        Editor,
        Uploads,
        Windows,
        Video,
    }

    public interface IPage : IDisposable
    {
        event EventHandler Closed;
        //void Close();
    }

    public interface IVideoCapturePage : IPage
    {
        void Open(ScreenRect captureArea);
    }

    public interface ILiveDrawPage : IPage
    {
        void Open();
    }

    public interface IScreenCapturePage : IPage
    {
        void Open();
        void Open(ScreenRect captureArea);
        void Open(IntPtr captureWindow);
    }

    public interface IPageManager
    {
        IVideoCapturePage CreateVideoCapturePage();
        IScreenCapturePage CreateScreenCapturePage();
        ILiveDrawPage CreateLiveDrawPage();
    }
}
