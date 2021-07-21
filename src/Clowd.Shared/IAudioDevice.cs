using System;

namespace Clowd
{
    public interface IAudioDevice
    {
        string DeviceId { get; }
        string FriendlyName { get; }
        IAudioLevelListener GetLevelListener();
    }

    public interface IAudioSpeakerDevice : IAudioDevice { }

    public interface IAudioMicrophoneDevice : IAudioDevice { }

    public interface IAudioLevelListener : IDisposable
    {
        double GetPeakLevel();
    }
}
