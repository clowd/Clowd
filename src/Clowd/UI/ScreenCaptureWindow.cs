using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Clowd.Capture;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.UI.Unmanaged;
using Clowd.Util;
using Rectangle = System.Drawing.Rectangle;

namespace Clowd.UI
{
    internal static class ScreenCaptureWindow
    {
        static FloatingButtonWindow _floating;
        static SettingsRoot _settings => SettingsRoot.Current;

        static readonly object _lock = new object();

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
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.U),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Photo",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconPhoto),
                    Executed = OnPhoto,
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.E),
                        new GlobalKeyGesture(Key.P),
                        new GlobalKeyGesture(Key.Enter),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Video",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconVideo),
                    Executed = OnVideo,
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.V),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Copy",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconCopy),
                    Executed = OnCopy,
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.C),
                        new GlobalKeyGesture(Key.C, ModifierKeys.Control),
                        new GlobalKeyGesture(Key.Insert, ModifierKeys.Control),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Primary = true,
                    Text = "_Save",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconSave),
                    Executed = OnSave,
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.S),
                        new GlobalKeyGesture(Key.S, ModifierKeys.Control),
                        new GlobalKeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Text = "_Reset",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconReset),
                    Executed = OnReset,
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.R),
                    }.ToList()
                });

                _buttons.Add(new CaptureToolButton
                {
                    Text = "E_xit",
                    IconPath = AppStyles.GetIconElement(ResourceIcon.IconClose),
                    Executed = OnExit,
                    Gestures = new GlobalKeyGesture[]
                    {
                        new GlobalKeyGesture(Key.X),
                        new GlobalKeyGesture(Key.Escape),
                    }.ToList()
                });

                _floating = FloatingButtonWindow.Create(_buttons);
            }
        }

        static ScreenCaptureWindow()
        {
            CaptureWindow.Disposed += SynchronizationContextEventHandler.CreateDelegate<DxDisposedEventArgs>(CaptureDisposed);
            CaptureWindow.LayoutUpdated += SynchronizationContextEventHandler.CreateDelegate<DxLayoutUpdatedEventArgs>(CaptureLayoutUpdated);
            CaptureWindow.KeyDown += SynchronizationContextEventHandler.CreateDelegate<DxKeyDownEventArgs>(CaptureKeyDown);
            CaptureWindow.ColorCaptured += SynchronizationContextEventHandler.CreateDelegate<DxColorCapturedEventArgs>(CaptureColorCaptured);
        }

        static void OnUpload(object sender, EventArgs e)
        {
            var session = GetSessionAndDispose();
            if (session != null)
                UploadManager.UploadImage(File.OpenRead(session.PreviewImgPath), "png", viewName: "Screenshot");
        }

        static void OnPhoto(object sender, EventArgs e)
        {
            var session = GetSessionAndDispose();
            if (session != null)
                EditorWindow.ShowSession(session);
        }

        static void OnVideo(object sender, EventArgs e)
        {
            var sel = CaptureWindow.GetSelection();
            if (sel == null || sel.IsEmpty()) return;

            DisposeInternal();
            var manager = App.GetService<IPageManager>();
            var video = manager.CreateVideoCapturePage();
            video.Open(sel);
        }

        static void OnReset(object sender, EventArgs e)
        {
            CaptureWindow.Reset();
        }

        static void OnCopy(object sender, EventArgs e)
        {
            CaptureWindow.WriteToClipboard();
            DisposeInternal();
        }

        static async void OnSave(object sender, EventArgs e)
        {
            var session = GetSessionAndDispose();
            if (session != null)
            {
                var filename = await NiceDialog.ShowSelectSaveFileDialog(_floating, "Save Screenshot", _settings.General.LastSavePath, "screenshot", "png");
                File.Copy(session.PreviewImgPath, filename);
                Platform.Current.RevealFileOrFolder(filename);
                _settings.General.LastSavePath = Path.GetDirectoryName(filename);
            }
        }

        static void OnExit(object sender, EventArgs e)
        {
            DisposeInternal();
        }

        public static void Open()
        {
            lock (_lock)
            {
                if (_floating == null)
                {
                    PrepareFloatingWindow();
                }

                // create new capture
                var options = new CaptureWindowOptions()
                {
                    AccentColor = AppStyles.AccentColor,
                    TipsDisabled = _settings.Capture.HideTipsPanel,
                };

                CaptureWindow.Show(options);
            }
        }

        public static void Close()
        {
            lock (_lock)
            {
                DisposeInternal();
            }
        }

        private static SessionInfo GetSessionAndDispose()
        {
            var dir = SessionManager.Current.CreateNewSessionDirectory();
            CaptureWindow.SaveSession(dir);

            var session = SessionManager.Current.GetSessionFromPath(dir);
            if (session != null)
            {
                session.Name = "Capture";
                //session.Icon = ModernWpf.Controls.Symbol.Camera;
            }

            DisposeInternal();
            return session;
        }

        private static void DisposeInternal()
        {
            _floating.HidePanel();
            CaptureWindow.Close();
        }

        private static void CaptureKeyDown(object sender, DxKeyDownEventArgs e)
        {
            _floating?.ProcessKey(KeyInterop.KeyFromVirtualKey(e.KeyCode));
        }

        private static void CaptureDisposed(object sender, DxDisposedEventArgs e)
        {
            //_wdxc = null;
            //Closed?.Invoke(this, new EventArgs());

            if (e.Error != null)
            {
                _floating.Dispatcher.Invoke(() =>
                {
                    NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, e.Error.ToString(), "An error occurred while showing screen capture window");
                });
            }
        }

        private static void CaptureLayoutUpdated(object sender, DxLayoutUpdatedEventArgs e)
        {
            if (e.Captured)
            {
                _floating.ShowPanel((ScreenRect)e.Selection);
            }
            else
            {
                _floating.HidePanel();
            }
        }

        private static void CaptureColorCaptured(object sender, DxColorCapturedEventArgs e)
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
