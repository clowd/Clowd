using Clowd.Video.FFmpeg;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Video
{
    public class FFmpegCapturer : VideoCapturerBase
    {
        private LiveScreenRecording _recorder;
        private Task _process;
        public override Task<string> StartAsync(Rectangle captureRect, VideoCapturerSettings settings)
        {
            BusyStatus = "Starting...";
            _recorder = new LiveScreenRecording(captureRect, settings);
            _process = _recorder.Start().ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    OnCriticalError(t.Exception.ToString());
                }
            });
            IsRecording = true;
            BusyStatus = null;
            return Task.FromResult(_recorder.FileName);
        }

        public override async Task StopAsync()
        {
            await _recorder.Stop();
            await _process;
        }

        public override void WriteLogToFile(string fileName)
        {
            File.WriteAllText(fileName, _recorder.ConsoleLog);
        }

        public override void Dispose()
        {
            if (IsRecording)
            {
                try
                {
                    StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch { }
            }
        }
    }
}
