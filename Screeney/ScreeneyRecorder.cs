using Accord.Video.FFMPEG;
using AForge.Video;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Screeney.Audio;
using Screeney.Video;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Screeney
{
    public class ScreeneyRecorder : IDisposable
    {
        public RecordingState State { get; private set; }
        public event EventHandler<CaptureUpdateEventArgs> RecordingUpdate;

        private AudioDeviceRecorder[] _audioRecorders = new AudioDeviceRecorder[] { };
        private MMDevice[] _audioDeviceCache = null;
        private Rectangle _region;
        private IVideoSource _videoSource;
        private VideoFileWriter _videoWriter;
        private string _videoFileName;
        private Stopwatch _clock;
        private ConcurrentQueue<bitmapFrame> _frameQueue;
        private Task _encodingThread;
        private CancellationTokenSource _encodingSignal;
        private bool _videoInit;
        private bool _audioInit;

        public ScreeneyRecorder(Rectangle region)
        {
            _clock = new Stopwatch();
            _region = region;
            _frameQueue = new ConcurrentQueue<bitmapFrame>();
            State = RecordingState.Uninitialized;
        }

        public void StartRecording()
        {
            if (State != RecordingState.Paused)
                throw new InvalidOperationException("Can not call start if audio and video have not been initalized");

            _clock.Start();
            _videoSource.Start();
            foreach (var a in _audioRecorders)
                a.Start();

            State = RecordingState.Recording;
        }
        public void PauseRecording()
        {
            if (State == RecordingState.Paused)
                return;
            if (State != RecordingState.Recording)
                throw new InvalidOperationException("Can not call pause if not recording.");

            _clock.Stop();
            _videoSource.Stop();
            foreach (var a in _audioRecorders)
                a.Stop();

            State = RecordingState.Paused;
        }
        public void StopRecording()
        {
            if (!(State == RecordingState.Recording || State == RecordingState.Paused))
                throw new InvalidOperationException("Can not call stop if not recording.");
            _clock.Stop();
            _videoSource.Stop();
            foreach (var a in _audioRecorders)
            {
                a.Stop();
                a.Dispose();
            }
            _videoWriter.Close();
            _videoWriter.Dispose();

            State = RecordingState.Stopped;
        }
        public Task StartEncoding(string outputFile)
        {
            var s = new NReco.VideoConverter.OutputSettings();
            s.AudioCodec = "libmp3lame";
            s.AudioSampleRate = 44100;
            s.VideoFrameSize = NReco.VideoConverter.FrameSize.hd720;
            s.VideoCodec = "libx264";
            s.CustomOutputArgs = "-preset veryslow";
            return StartEncoding(outputFile, s);
        }
        public Task StartEncoding(string outputFile, NReco.VideoConverter.OutputSettings settings)
        {
            if (State != RecordingState.Stopped)
                throw new InvalidOperationException("Can not call encode if video has not been captured and stopped.");

            State = RecordingState.Encoding;

            return Task.Factory.StartNew(() =>
            {
                string[] audioFiles = _audioRecorders.Select(r => r.FileName).ToArray();
                string videoFile = _videoFileName;
                string finalAudio = null;
                var ex = new FFMpegConverterEx();
                ex.FFMpegProcessPriority = ProcessPriorityClass.BelowNormal;
                if(audioFiles.Length > 1)
                {
                    finalAudio = Path.GetTempFileName() + ".wav";
                    ex.MergeAudio(audioFiles, finalAudio);
                    foreach (var f in audioFiles)
                        File.Delete(f);
                }
                else if (audioFiles.Length == 1)
                    finalAudio = audioFiles[0];

                ex.CompileVideo(finalAudio, videoFile, outputFile, settings);
                File.Delete(finalAudio);
                File.Delete(videoFile);

                State = RecordingState.Finished;
            });
        }

        public void InitVideo(CaptureVideoSource source, int framerate)
        {
            if (State != RecordingState.Uninitialized)
                throw new InvalidOperationException("Can not call init if state is not uninitialized");
            _videoInit = true;

            switch (source.Source)
            {
                case CaptureVideoSource.SourceID.Default_GDI:
                    _videoSource = new GdiCaptureSource(_region, 1000 / framerate);
                    break;
                default:
                    _videoSource = new GdiCaptureSource(_region, 1000 / framerate);
                    break;
            }
            var bitrate = Math.Min(_region.Width * _region.Height * framerate, 6000000);
            _videoFileName = Path.GetTempFileName() + ".avi";
            _videoWriter = new VideoFileWriter();
            _videoWriter.Open(_videoFileName, _region.Width, _region.Height, framerate, VideoCodec.MPEG4, bitrate);
            _videoSource.NewFrame += _videoSource_NewFrame;
            _encodingSignal = new CancellationTokenSource();
            _encodingThread = Task.Factory.StartNew(() => encode(_encodingSignal.Token));

            if (_audioInit)
                State = RecordingState.Paused;
        }
        public CaptureVideoSource[] GetVideoSources()
        {
            List<CaptureVideoSource> sources = new List<CaptureVideoSource>();

            //only can offer improved capture methods if the entire region is contained within one monitor.
            var screen = MonitorUtil.GetScreenContainingRect(_region);
            if (screen.WorkingArea.Contains(_region))
            {
                if (Environment.OSVersion.Version >= new Version(6, 2, 0))
                {
                    //sources.Add(new CaptureVideoSource(CaptureVideoSource.SourceID.Duplication_API));
                }
                if (Environment.OSVersion.Version == new Version(6, 1))
                {
                    //sources.Add(new CaptureVideoSource(CaptureVideoSource.SourceID.Mirror_Driver));
                }
                //sources.Add(new CaptureVideoSource(CaptureVideoSource.SourceID.DirectX));
                //sources.Add(new CaptureVideoSource(CaptureVideoSource.SourceID.IDirect3DDevice9_Hook));
            }

            sources.Add(new CaptureVideoSource(CaptureVideoSource.SourceID.Default_GDI));
            return sources.ToArray();
        }

        public void InitAudio(params CaptureAudioSource[] devices)
        {
            if (State != RecordingState.Uninitialized)
                throw new InvalidOperationException("Can not call init if state is not uninitialized");

            _audioInit = true;
            if (devices == null || devices.Length < 1)
                return;

            _audioRecorders = _audioDeviceCache.Where(c => devices.Any(d => d.ID == c.ID))
                .Select(d => new AudioDeviceRecorder(Path.GetTempFileName() + ".wav", d)).ToArray();

            if (_videoInit)
                State = RecordingState.Paused;
        }
        public CaptureAudioSource[] GetAudioSources()
        {
            var mm_devices = new List<MMDevice>(new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active));
            _audioDeviceCache = mm_devices.ToArray();
            return mm_devices.Select(d => new CaptureAudioSource(d.ID, d.DataFlow == DataFlow.Render, d.FriendlyName)).ToArray();
        }

        private void _videoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            _frameQueue.Enqueue(new bitmapFrame() { frame = eventArgs.Frame, clock = _clock.Elapsed });
        }
        private void encode(CancellationToken token)
        {
            int frameCount = 0;
            int frameDroppedCount = 0;
            DateTime _lastFrameMessage = DateTime.Now;
            int lastFrames = 0;
            while (!token.IsCancellationRequested)
            {
                if (_frameQueue.Count < 1)
                {
                    if (State == RecordingState.Recording
                        || State == RecordingState.Paused)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    //Console.WriteLine($"final frame count:{frameCount} in {_clock.Elapsed.TotalSeconds}s");
                    return;
                }
                bitmapFrame frame;

                //drop frames if we're behind
                var overage = _frameQueue.Count / 10;
                for (int i = 0; i < overage; i++)
                {
                    frameDroppedCount++;
                    frameCount++;
                    _frameQueue.TryDequeue(out frame);
                }

                if (_frameQueue.TryDequeue(out frame))
                {
                    frameCount++;
                    if (DateTime.Now - _lastFrameMessage > TimeSpan.FromSeconds(1))
                    {
                        _lastFrameMessage = DateTime.Now;
                        RecordingUpdate?.Invoke(this, new CaptureUpdateEventArgs(
                            frameCount, frameDroppedCount, _frameQueue.Count, frameCount - lastFrames,
                            frameCount / (int)_clock.Elapsed.TotalSeconds, _clock.Elapsed));
                        lastFrames = frameCount;
                    }
                    _videoWriter.WriteVideoFrame(frame.frame, frame.clock);
                }
                else
                {
                    //Console.WriteLine($"Unspecified frame dequeue error.");
                    Thread.Sleep(50);
                }
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                }
                disposedValue = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion

        private class bitmapFrame
        {
            public Bitmap frame { get; set; }
            public TimeSpan clock { get; set; }
        }
    }
    public class CaptureUpdateEventArgs
    {
        public int FramesCaptured { get; private set; }
        public int FramesDropped { get; private set; }
        public int EncodingBacklog { get; private set; }
        public TimeSpan Clock { get; private set; }
        public int FpsCurrent { get; private set; }
        public int FpsAverage { get; private set; }

        public CaptureUpdateEventArgs(int capture, int dropped, int backlock, int fps, int avg_fps, TimeSpan clock)
        {
            FramesCaptured = capture;
            FramesDropped = dropped;
            EncodingBacklog = backlock;
            Clock = clock;
            FpsCurrent = fps;
            FpsAverage = avg_fps;
        }

        public override string ToString()
        {
            return $"{Clock.ToString()}: {FramesCaptured} ({FramesDropped} dropped). {FpsCurrent}fps ({FpsAverage} avg).";
        }
    }
    public enum RecordingState
    {
        Uninitialized,
        Recording,
        Paused,
        Stopped,
        Encoding,
        Finished
    }
    public class CaptureVideoSource
    {
        public int ID { get; private set; }
        public string Name { get; private set; }

        internal SourceID Source { get; private set; }

        internal CaptureVideoSource(SourceID type)
        {
            ID = (int)type;
            Name = type.ToString().Replace('_', ' ');
            Source = type;
        }
        internal enum SourceID
        {
            Default_GDI = 0,
            Duplication_API = 1,
            Mirror_Driver = 2,
            DirectX = 3,
            IDirect3DDevice9_Hook = 4,
        }
    }
    public class CaptureAudioSource
    {
        public string ID { get; private set; }
        public bool IsLoopback { get; private set; }
        public string Name { get; private set; }
        internal CaptureAudioSource(string id, bool loopback, string name)
        {
            ID = id;
            IsLoopback = loopback;
            Name = name;
        }
    }
}
