using System;

namespace Clowd.Video.FFmpeg
{
    /// <summary>Provides data for log received event</summary>
    public class FFMpegLogEventArgs : EventArgs
    {
        /// <summary>Log line</summary>
        public string Data { get; private set; }

        public FFMpegLogEventArgs(string logData)
        {
            this.Data = logData;
        }
    }
}
