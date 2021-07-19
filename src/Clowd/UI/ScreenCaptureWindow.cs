using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Clowd.Capture;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.Util;

namespace Clowd.UI
{
    internal sealed class ScreenCaptureWindow : IScreenCapturePage
    {
        static ClowdWin64.DxScreenCapture _wdxc;
        static FloatingButtonWindow _floating;
        static ClowdSettings _settings => ClowdSettings.Current;

        static readonly object _lock = new object();

        public event EventHandler Closed;

        internal static void PrepareFloatingWindow()
        {
            lock (_lock)
            {
                if (_floating != null)
                {
                    _floating.Close();
                    _floating = null;
                }

                var _buttons = new List<CaptureToolButton>();

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Upload",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconClowd),
                    Executed = OnUpload,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.U),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Photo",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconPhoto),
                    Executed = OnPhoto,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.E),
                        new StorableKeyGesture(Key.P),
                        new StorableKeyGesture(Key.Enter),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Video",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconVideo),
                    Executed = OnVideo,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.V),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Copy",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconCopy),
                    Executed = OnCopy,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.C),
                        new StorableKeyGesture(Key.C, ModifierKeys.Control),
                        new StorableKeyGesture(Key.Insert, ModifierKeys.Control),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Save",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconSave),
                    Executed = OnSave,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.S),
                        new StorableKeyGesture(Key.S, ModifierKeys.Control),
                        new StorableKeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Text = "_Reset",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconReset),
                    Executed = OnReset,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.R),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Text = "E_xit",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconClose),
                    Executed = OnExit,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.X),
                        new StorableKeyGesture(Key.Escape),
                    }.ToList()
                });

                _floating = FloatingButtonWindow.Create(_buttons);
            }
        }

        static void OnUpload(object sender, EventArgs e)
        {
            var session = GetSessionAndDispose();
            if (session != null)
                UploadManager.UploadImage(File.OpenRead(session.CroppedPath), "png", viewName: "Screenshot");
        }

        static void OnPhoto(object sender, EventArgs e)
        {
            var session = GetSessionAndDispose();
            if (session != null)
                EditorWindow.ShowSession(session);
        }

        static void OnVideo(object sender, EventArgs e)
        {
            var sel = _wdxc?.Selection;
            if (sel == null) return;

            DisposeInternal();
            var manager = App.GetService<IPageManager>();
            var video = manager.CreateVideoCapturePage();
            video.Open(ScreenRect.FromSystem(sel.Value));
        }

        static void OnReset(object sender, EventArgs e)
        {
            _wdxc?.Reset();
        }

        static void OnCopy(object sender, EventArgs e)
        {
            _wdxc?.WriteToClipboard();
            DisposeInternal();
        }

        static async void OnSave(object sender, EventArgs e)
        {
            var session = GetSessionAndDispose();
            if (session != null)
            {
                var filename = await NiceDialog.ShowSelectSaveFileDialog(_floating, "Save Screenshot", _settings.General.LastSavePath, "screenshot", "png");
                File.Copy(session.CroppedPath, filename);
                Platform.Current.RevealFileOrFolder(filename);
                _settings.General.LastSavePath = Path.GetDirectoryName(filename);
            }
        }

        static void OnExit(object sender, EventArgs e)
        {
            DisposeInternal();
        }

        public void Open()
        {
            OpenInternal();
        }

        public void Open(ScreenRect captureArea)
        {
            OpenInternal(rect: captureArea.ToSystem());
        }

        public void Open(IntPtr captureWindow)
        {
            OpenInternal(wnd: captureWindow);
        }

        private void OpenInternal(System.Drawing.Rectangle? rect = null, IntPtr? wnd = null)
        {
            lock (_lock)
            {
                if (_wdxc != null)
                    return;

                if (_floating == null)
                {
                    PrepareFloatingWindow();
                    //throw new InvalidOperationException("Floating window does not exist. Please create it first.");
                }

                var wpfclr = AppStyles.AccentColor;
                var clr = System.Drawing.Color.FromArgb(wpfclr.A, wpfclr.R, wpfclr.G, wpfclr.B);

                // create new capture
                var options = new ClowdWin64.ScreenCaptureOptions()
                {
                    AccentColor = clr,
                    TipsDisabled = _settings.Capture.HideTipsPanel,
                };

                var dx = new ClowdWin64.DxScreenCapture(options);
                dx.Disposed += SynchronizationContextEventHandler.CreateDelegate<ClowdWin64.DxDisposedEventArgs>(CaptureDisposed);
                dx.LayoutUpdated += SynchronizationContextEventHandler.CreateDelegate<ClowdWin64.DxLayoutUpdatedEventArgs>(CaptureLayoutUpdated);
                dx.KeyDown += SynchronizationContextEventHandler.CreateDelegate<ClowdWin64.DxKeyDownEventArgs>(CaptureKeyDown);
                dx.ColorCaptured += SynchronizationContextEventHandler.CreateDelegate<ClowdWin64.DxColorCapturedEventArgs>(CaptureColorCaptured);
                _wdxc = dx;
            }
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                DisposeInternal();
            }
        }

        private static SessionInfo GetSessionAndDispose()
        {
            var session = SessionUtil.Parse(_wdxc?.SaveSession(SessionUtil.CreateNewSessionDirectory()));
            DisposeInternal();
            return session;
        }

        private static void DisposeInternal()
        {
            _floating.HidePanel();
            _wdxc?.Close();
        }

        private void CaptureKeyDown(object sender, ClowdWin64.DxKeyDownEventArgs e)
        {
            _floating?.ProcessKey(KeyInterop.KeyFromVirtualKey(e.KeyCode));
        }

        private void CaptureDisposed(object sender, ClowdWin64.DxDisposedEventArgs e)
        {
            _wdxc = null;
            Closed?.Invoke(this, new EventArgs());

            if (e.Error != null)
            {
                _floating.Dispatcher.Invoke(() =>
                {
                    NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, e.Error.ToString(), "An unhandled error occurred while showing screen capture window");
                });
            }
        }

        private void CaptureLayoutUpdated(object sender, ClowdWin64.DxLayoutUpdatedEventArgs e)
        {
            if (e.Captured)
            {
                _floating.ShowPanel(ScreenRect.FromSystem(e.Selection));
            }
            else
            {
                _floating.HidePanel();
            }
        }

        private void CaptureColorCaptured(object sender, ClowdWin64.DxColorCapturedEventArgs e)
        {
            DisposeInternal();
            _floating?.HidePanel();
            _floating.Dispatcher.Invoke(() =>
            {
                NiceDialog.ShowColorDialogAsync(null, System.Windows.Media.Color.FromRgb(e.Color.R, e.Color.G, e.Color.B));
            });
        }
    }
}
