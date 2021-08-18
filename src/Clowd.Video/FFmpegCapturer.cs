using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.Video.FFmpeg;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Clowd.Video
{
    public class FFmpegCapturer : VideoCapturerBase
    {
        private LiveScreenRecording _recorder;
        private Task _process;

        public override Task<string> StartAsync(ScreenRect captureRect, VideoSettings settings)
        {
            BusyStatus = "Starting...";
            _recorder = new LiveScreenRecording(captureRect, settings);
            _recorder.LogReceived += Recording_LogRecieved;
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

        private void Recording_LogRecieved(object sender, FFMpegLogEventArgs e)
        {
            //frame=  219 fps= 31 q=10.0 size=       0kB time=00:00:05.80 bitrate=   0.1kbits/s dup=5 drop=0 speed=0.82x

            string getData(string label)
            {
                var msg = e.Data;
                var start = msg.IndexOf(label);
                if (start < 0)
                    return null;
                msg = msg.Substring(start + label.Length).TrimStart();
                msg = msg.Substring(0, msg.IndexOf(" "));
                if (msg == "0.0" || msg == "00:00:00.00")
                    return null;

                return msg;
            }

            var fps = getData("fps=");
            var time = getData("time=");
            TimeSpan ts = default(TimeSpan);

            if (time != null)
            {
                try
                {
                    // sometimes ffmpeg gives us garbage timecodes, it depends on the input stream timestamp on the frames & the settings we use.
                    ts = TimeSpan.Parse(time);
                }
                catch { }
            }

            OnStatusRecieved((int)Convert.ToDouble(fps), 0, ts);
        }
    }
}
