using System;

namespace Clowd.Video
{
    /// <summary>
    /// The exception that is thrown when FFMpeg process retruns non-zero error exit code
    /// </summary>
    public class FFMpegException : Exception
    {
        /// <summary>Get FFMpeg process error code</summary>
        public int ErrorCode { get; private set; }

        public FFMpegException(int errCode, string message)
          : base(string.Format("{0} (exit code: {1})", (object)message, (object)errCode))
        {
            this.ErrorCode = errCode;
        }
    }
}
