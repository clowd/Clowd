using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screeney.Video
{
    internal interface IVideoSource
    {
        event NewFrameEventHandler NewFrame;
        event VideoSourceErrorEventHandler VideoSourceError;
        event PlayingFinishedEventHandler PlayingFinished;
        string Source { get; }
        int FramesReceived { get; }
        long BytesReceived { get; }
        bool IsRunning { get; }
        void Start();
        void SignalToStop();
        void WaitForStop();
        void Stop();
    }
}
