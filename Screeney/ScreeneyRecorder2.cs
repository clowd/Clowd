using AForge.Video;
using NAudio.CoreAudioApi;
using Screeney.Audio;
using Screeney.Video;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;
using Accord.Video.FFMPEG;
using System.Diagnostics;
using System.Timers;

namespace Screeney
{
    public class ScreeneyRecorder2 : IDisposable
    {
        public event EventHandler<CaptureUpdateEventArgs> RecordingUpdate;
        public string FilePath { get { return _filePath; } }
        public bool IsRunning { get; private set; }

        private WasapiAudioProvider _audio;
        private IVideoSource _video;
        private VideoFileWriter _writer;
        private string _filePath;
        private Stopwatch _clock;
        private int _frames;
        private int _lastFrames;
        private Timer _timer;

        public ScreeneyRecorder2(Rectangle region, int framerate = 20, MMDevice audio = null, string filePath = null)
        {
            _timer = new Timer(1000);
            _timer.Elapsed += TimerElapsed;
            _video = new GdiCaptureSource(region, 1000 / framerate);
            _filePath = filePath ?? System.IO.Path.GetTempFileName() + ".mp4";

            _writer = new VideoFileWriter();
            var vBitrate = Math.Min(region.Width * region.Height * framerate, 5000000);
            if (audio != null)
            {
                _audio = new WasapiAudioProvider(audio);
                var aBitrate = _audio.WaveFormat.BitsPerSample * _audio.WaveFormat.SampleRate * _audio.WaveFormat.Channels;
                _writer.Open(filePath, region.Width, region.Height, framerate, VideoCodec.Default, vBitrate,
                    AudioCodec.AAC, aBitrate, _audio.WaveFormat.SampleRate, _audio.WaveFormat.Channels);
                _audio.DataAvailable += AudioRecieved;
            }
            else
            {
                _writer.Open(filePath, region.Width, region.Height, framerate, VideoCodec.Default, vBitrate);
            }
            _video.NewFrame += VideoRecieved;
        }

        public void Start()
        {
            if (IsRunning)
                return;
            IsRunning = true;
            _timer.Start();
            _clock.Start();
            _video.Start();
            if (_audio != null)
                _audio.Start();
        }
        public void Stop()
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            _timer.Stop();
            _clock.Stop();
            _video.Stop();
            if (_audio != null)
                _audio.Stop();
        }
        public void Close()
        {
            Stop();
            if (_writer.IsOpen)
                _writer.Close();
        }
        public void Dispose()
        {
            Close();
        }

        private void TimerElapsed(object sender, ElapsedEventArgs e)
        {
            RecordingUpdate?.Invoke(this, new CaptureUpdateEventArgs(
                            _frames, 0, 0, _frames - _lastFrames,
                            _frames / (int)_clock.Elapsed.TotalSeconds, _clock.Elapsed));
            _lastFrames = _frames;
        }
        private void VideoRecieved(object sender, NewFrameEventArgs e)
        {
            _frames++;
            _writer.WriteVideoFrame(e.Frame, _clock.Elapsed);
        }

        private void AudioRecieved(object sender, WaveInEventArgs e)
        {
            byte[] arr = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, 0, arr, 0, arr.Length);
            _writer.WriteAudioFrame(arr);
        }
    }
}
