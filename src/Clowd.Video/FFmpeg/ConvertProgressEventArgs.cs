using System;

namespace Clowd.Video.FFmpeg
{
    /// <summary>Provides data for ConvertProgress event</summary>
    public class ConvertProgressEventArgs : EventArgs
    {
        /// <summary>Total media stream duration</summary>
        public TimeSpan TotalDuration { get; private set; }

        /// <summary>Processed media stream duration</summary>
        public TimeSpan Processed { get; private set; }

        public ConvertProgressEventArgs(TimeSpan processed, TimeSpan totalDuration)
        {
            this.TotalDuration = totalDuration;
            this.Processed = processed;
        }
    }
}
