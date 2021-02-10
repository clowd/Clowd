using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public interface IAudioDevice
    {
        string DeviceId { get; }
        string FriendlyName { get; }
    }

    public interface IAudioSpeakerDevice : IAudioDevice { }
    public interface IAudioMicrophoneDevice : IAudioDevice { }
}
