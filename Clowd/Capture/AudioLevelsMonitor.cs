using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Clowd.Capture
{
    public class AudioLevelsMonitor : IDisposable, INotifyPropertyChanged
    {
        public double SpeakerPeakLevel { get; private set; }
        public double MicPeakLevel { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public event EventHandler ReinitNeeded;

        System.Timers.Timer timer;
        //DispatcherTimer timer;
        NAudioItem speaker;
        NAudioItem mic;
        VideoSettings settings;
        int exceptionCount;

        public AudioLevelsMonitor(VideoSettings settings)
        {
            this.settings = settings;
            Initialize();
        }

        public void Initialize()
        {
            Dispose();

            exceptionCount = 0;
            timer = new System.Timers.Timer(20);
            timer.Elapsed += Timer_Tick;
            timer.AutoReset = true;
            timer.Enabled = true;

            if (settings.VideoCodec.GetSelectedPreset() is FFmpegCodecPreset_AudioBase audio)
            {
                if (audio.SelectedMicrophone != null)
                {
                    try
                    {
                        mic = NAudioItem.Microphones.FirstOrDefault(m => m.Name == audio.SelectedMicrophone.FriendlyName);
                    }
                    catch { }
                    mic?.StartListeningForPeakLevel();
                }

                if (audio.IsDirectShowInstalled)
                {
                    try
                    {
                        speaker = NAudioItem.DefaultSpeaker;
                    }
                    catch { }
                    speaker?.StartListeningForPeakLevel();
                }
            }

            timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (settings.VideoCodec.GetSelectedPreset() is FFmpegCodecPreset_AudioBase audio && audio.SelectedMicrophone?.FriendlyName != mic?.Name)
            {
                Initialize();
                return;
            }

            var oldSpk = SpeakerPeakLevel;
            var oldMic = MicPeakLevel;

            try
            {
                SpeakerPeakLevel = speaker != null ? (speaker.PeakLevel * 100) : 0;
            }
            catch (InvalidCastException)
            {
                SpeakerPeakLevel = 0;
                exceptionCount++;
            }

            try
            {
                MicPeakLevel = mic != null ? (mic.PeakLevel * 100) : 0;
            }
            catch (InvalidCastException)
            {
                MicPeakLevel = 0;
                exceptionCount++;
            }

            if (oldSpk != SpeakerPeakLevel)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakerPeakLevel)));

            if (oldMic != MicPeakLevel)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MicPeakLevel)));

            if (exceptionCount > 10)
            {
                Dispose();
                ReinitNeeded?.Invoke(this, new EventArgs());
            }
        }

        public void Dispose()
        {
            if (timer != null)
            {
                timer.Stop();
                timer.Dispose();
                timer = null;
            }

            if (speaker != null)
            {
                speaker.Dispose();
                speaker = null;
            }

            if (mic != null)
            {
                mic.Dispose();
                mic = null;
            }
        }
    }
}
