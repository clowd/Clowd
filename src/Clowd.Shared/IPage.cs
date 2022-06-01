using Clowd.PlatformUtil;
using System;
using System.Threading.Tasks;

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
        bool IsRecording { get; }
        void Open(ScreenRect captureArea);
        Task StartRecording();
        Task StopRecording();
    }

    public interface ILiveDrawPage : IPage
    {
        void Open();
    }

    public interface IScreenCapturePage : IPage
    {
        void Open(ScreenRect captureArea);
    }
}
