using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.Util;
using Clowd.Video;
using PropertyChanged.SourceGenerator;

namespace Clowd.UI.Helpers
{
    internal partial class ObsViewWrapper : IDisposable
    {
        public ScreenRect Selection { get; }

        public string FileName { get; }

        public string PrimaryText
        {
            get
            {
                if (Started) return "";
                if (Initializing) return "WAIT...";
                return Initialized ? "START" : "RELOAD";
            }
        }

        public bool CanPrimary => MustReload || CanStart;

        public bool MustReload => !Started && !Initialized && !Initializing;

        private bool CanStart => !Started && Initialized;

        public event EventHandler PrimaryTextChanged;
        public event EventHandler OutputChanged;
        public event EventHandler ListenerChanged;
        public event EventHandler<VideoCriticalErrorEventArgs> CriticalError;
        public event EventHandler<VideoStatusEventArgs> StatusReceived;

        [Notify(Setter.Private)]
        private bool _initialized;

        [Notify(Setter.Private)]
        private bool _initializing;

        [Notify(Setter.Private)]
        private bool _started;

        [Notify(Setter.Private)]
        private bool _recording;

        private bool _disposed;
        private SettingsVideo _settings = SettingsRoot.Current.Video;
        private ObsCapturer _capturer;
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public ObsViewWrapper(string outputFile, ScreenRect selection)
        {
            Selection = selection;
            FileName = outputFile;
            _settings.PropertyChanged += SettingChanged;
        }

        public async Task Initialize()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || Started || Initialized) return;

                Initializing = true;
                Initialized = false;

                if (_capturer != null)
                    await _capturer.DisposeAsync();

                _capturer = new ObsCapturer();
                _capturer.CriticalError += SynchronizationContextEventHandler.CreateDelegate<VideoCriticalErrorEventArgs>(CapturerCriticalError);
                _capturer.StatusReceived += SynchronizationContextEventHandler.CreateDelegate<VideoStatusEventArgs>(CapturerStatusReceived);
                await _capturer.Initialize(FileName, Selection, _settings);

                Initialized = true;
                Initializing = false;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void OnPrimaryTextChanged()
        {
            PrimaryTextChanged?.Invoke(this, new EventArgs());
        }

        private void CapturerStatusReceived(object sender, VideoStatusEventArgs e)
        {
            StatusReceived?.Invoke(sender, e);
        }

        private void CapturerCriticalError(object sender, VideoCriticalErrorEventArgs e)
        {
            CriticalError?.Invoke(sender, e);
        }

        public async Task Start()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || Started || !Initialized) return;
                Started = true;
                await _capturer.StartAsync();
                Recording = true;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task Stop()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || !Recording) return;
                await _capturer.StopAsync();
                Recording = false;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void SettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SettingsVideo.OpenFinishedInExplorer) or nameof(SettingsVideo.FilenamePattern) or nameof(SettingsVideo.OutputDirectory))
            {
                // do nothing
            }
            else if (e.PropertyName is nameof(SettingsVideo.OutputMode))
            {
                OutputChanged?.Invoke(this, new EventArgs());
            }
            else if (e.PropertyName is nameof(SettingsVideo.CaptureSpeakerDevice) or nameof(SettingsVideo.CaptureMicrophoneDevice))
            {
                ListenerChanged?.Invoke(this, new EventArgs());
            }
            else if (e.PropertyName is nameof(SettingsVideo.CaptureSpeaker) or nameof(SettingsVideo.CaptureMicrophone))
            {
                _capturer?.SetMicrophoneMute(!_settings.CaptureMicrophone);
                _capturer?.SetSpeakerMute(!_settings.CaptureSpeaker);
                ListenerChanged?.Invoke(this, new EventArgs());
            }
            else if (Initialized || !Started)
            {
                Invalidate();
            }
        }

        private async void Invalidate()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_disposed || Started || !Initialized) return;

                if (_capturer != null)
                {
                    await _capturer.DisposeAsync();
                    _capturer = null;
                }

                Initialized = false;
            }
            catch (Exception e)
            {
                CapturerCriticalError(_capturer, new VideoCriticalErrorEventArgs(e.ToString()));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.PropertyChanged -= SettingChanged;
            _semaphore.WaitAsync(3000).ContinueWith((t) =>
            {
                _semaphore.Dispose();
                _capturer?.Dispose();
                _capturer = null;
            });
        }
    }
}
