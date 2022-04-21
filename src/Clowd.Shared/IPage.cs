using Clowd.PlatformUtil;
using System;

namespace Clowd
{
    public enum SettingsPageTab
    {
        NewItem,
        RecentSessions,
        Uploads,
        SettingsGeneral,
        SettingsHotkeys,
        SettingsCapture,
        SettingsEditor,
        SettingsUploads,
        SettingsVideo,
        About,
    }

    public interface IPage
    {
        event EventHandler Closed;
        void Close();
    }

    public interface ISettingsPage : IPage
    {
        void Open(SettingsPageTab? selectedTab = null);
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
}
