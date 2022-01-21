using System;

namespace Clowd
{
    public interface IAudioDevice
    {
        string DeviceId { get; }
        string DeviceType { get; }
        string FriendlyName { get; }
    }

    public interface IAudioSpeakerDevice : IAudioDevice { }

    public interface IAudioMicrophoneDevice : IAudioDevice { }

    public interface IAudioLevelListener : IDisposable
    {
        double GetPeakLevel();
    }
}
