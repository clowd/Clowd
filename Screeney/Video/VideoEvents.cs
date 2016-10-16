using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screeney.Video
{
    internal delegate void NewFrameEventHandler(object sender, NewFrameEventArgs eventArgs);
    internal delegate void VideoSourceErrorEventHandler(object sender, VideoSourceErrorEventArgs eventArgs);
    internal delegate void PlayingFinishedEventHandler(object sender, ReasonToFinishPlaying reason);
    internal enum ReasonToFinishPlaying
    {
        EndOfStreamReached,
        StoppedByUser,
        DeviceLost,
        VideoSourceError
    }
    internal class NewFrameEventArgs : EventArgs
    {
        private System.Drawing.Bitmap frame;

        public NewFrameEventArgs(System.Drawing.Bitmap frame)
        {
            this.frame = frame;
        }
        public System.Drawing.Bitmap Frame
        {
            get { return frame; }
        }
    }

    internal class VideoSourceErrorEventArgs : EventArgs
    {
        private string description;
        public VideoSourceErrorEventArgs(string description)
        {
            this.description = description;
        }
        public string Description
        {
            get { return description; }
        }
    }
}
