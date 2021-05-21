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
using Clowd.Interop;
using Clowd.UI.Helpers;
using Clowd.Util;
using ScreenVersusWpf;

namespace Clowd.UI
{
    sealed class SelectRegionWindow : IScreenCapturePage
    {
        static ClowdWin64.DxScreenCapture _wdxc;
        static FloatingButtonWindow _floating;
        static List<FloatingButtonDetail> _buttons;

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

                _buttons = new List<FloatingButtonDetail>();

                _buttons.Add(new FloatingButtonDetail
                {
                    Primary = true,
                    Enabled = true,
                    Label = "_Upload",
                    IconResourceName = "IconClowd",
                    Executed = OnUpload,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.U),
                    }
                });

                _buttons.Add(new FloatingButtonDetail
                {
                    Primary = true,
                    Enabled = true,
                    Label = "_Photo",
                    IconResourceName = "IconPhoto",
                    Executed = OnPhoto,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.E),
                        new StorableKeyGesture(Key.P),
                        new StorableKeyGesture(Key.Enter),
                    }
                });

                _buttons.Add(new FloatingButtonDetail
                {
                    Primary = true,
                    Enabled = true,
                    Label = "_Video",
                    IconResourceName = "IconVideo",
                    Executed = OnVideo,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.V),
                    }
                });

                _buttons.Add(new FloatingButtonDetail
                {
                    Primary = true,
                    Enabled = true,
                    Label = "_Copy",
                    IconResourceName = "IconCopy",
                    Executed = OnCopy,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.C),
                        new StorableKeyGesture(Key.C, ModifierKeys.Control),
                        new StorableKeyGesture(Key.Insert, ModifierKeys.Control),
                    }
                });

                _buttons.Add(new FloatingButtonDetail
                {
                    Primary = true,
                    Enabled = true,
                    Label = "_Save",
                    IconResourceName = "IconSave",
                    Executed = OnSave,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.S),
                        new StorableKeyGesture(Key.S, ModifierKeys.Control),
                        new StorableKeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift),
                    }
                });

                _buttons.Add(new FloatingButtonDetail
                {
                    Enabled = true,
                    Label = "_Reset",
                    IconResourceName = "IconReset",
                    Executed = OnReset,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.R),
                    }
                });

                _buttons.Add(new FloatingButtonDetail
                {
                    Enabled = true,
                    Label = "E_XIT",
                    IconResourceName = "IconClose",
                    Executed = OnExit,
                    Gestures = new StorableKeyGesture[]
                    {
                        new StorableKeyGesture(Key.X),
                        new StorableKeyGesture(Key.Escape),
                    }
                });

                _floating = FloatingButtonWindow.Create(_buttons);
            }
        }

        static void OnUpload(object sender, EventArgs e)
        {
            ProcessBitmap(async (s, b) =>
            {
                await _floating.ShowConfirmationRipple("Starting upload...");
                MemoryStream ms = new MemoryStream();
                b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var t = UploadManager.UploadImage(ms, "png", viewName: "Screenshot");
            });
        }

        static void OnPhoto(object sender, EventArgs e)
        {
            ProcessBitmap(async (s, b) =>
            {
                ImageEditorPage.ShowNewEditor(b, ScreenRect.FromSystem(s).ToWpfRect());
            });
        }

        static void OnVideo(object sender, EventArgs e)
        {
            ProcessBitmap(async (s, b) =>
            {
                var manager = App.GetService<IPageManager>();
                var video = manager.CreateVideoCapturePage();
                video.Open(ScreenRect.FromSystem(s));
            });
        }

        static void OnReset(object sender, EventArgs e)
        {
            _wdxc?.Reset();
        }

        static void OnCopy(object sender, EventArgs e)
        {
            ProcessBitmap(async (s, b) =>
            {
                var data = new ClipboardDataObject();
                data.SetImage(b);
                await data.SetClipboardData();
                await _floating.ShowConfirmationRipple("Copied to clipboard.");
            });
        }

        static async void OnSave(object sender, EventArgs e)
        {
            ProcessBitmap(async (s, b) =>
            {
                var filename = await NiceDialog.ShowSelectSaveFileDialog(_floating, "Save Screenshot", App.Current.Settings.LastSavePath, "screenshot", "png");
                if (!String.IsNullOrWhiteSpace(filename) && Directory.Exists(Path.GetDirectoryName(filename)))
                {
                    b.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
                    Interop.Shell32.WindowsExplorer.ShowFileOrFolder(filename);
                    App.Current.Settings.LastSavePath = Path.GetDirectoryName(filename);
                }
                _floating.HidePanel();
            });
        }

        static void OnExit(object sender, EventArgs e)
        {
            DisposeInternal();
            _floating.HidePanel();
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
                if (_floating == null)
                {
                    PrepareFloatingWindow();
                    //throw new InvalidOperationException("Floating window does not exist. Please create it first.");
                }

                var clr = System.Drawing.Color.FromArgb(App.Current.AccentColor.A, App.Current.AccentColor.R, App.Current.AccentColor.G, App.Current.AccentColor.B);

                // create new capture
                var options = new ClowdWin64.ScreenCaptureOptions()
                {
                    AccentColor = clr,
                };
                var dx = new ClowdWin64.DxScreenCapture(options);
                dx.Disposed += _wdxc_Disposed;
                dx.LayoutUpdated += _wdxc_LayoutUpdated;
                dx.KeyDown += _wdxc_KeyDown;
                dx.ColorCaptured += _wdxc_ColorCaptured;
                //dx.Show();

                // close old capture (if any)
                DisposeInternal();
                _floating.HidePanel();

                // assign new capture
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
                _floating.HidePanel();
            }
        }

        private static async void ProcessBitmap(Func<System.Drawing.Rectangle, BitmapSource, Task> action)
        {
            if (_wdxc == null) return;

            var sel = _wdxc.Selection;
            WriteableBitmap bmp = new WriteableBitmap(sel.Width, sel.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
            bmp.Lock();

            var size = bmp.BackBufferStride * bmp.PixelHeight;
            _wdxc.WriteToPointer(bmp.BackBuffer, size);
            _wdxc.Close();
            _wdxc = null;

            bmp.Unlock();
            bmp.Freeze();

            await action(sel, bmp);

            _floating?.HidePanel();
        }

        private static void DisposeInternal()
        {
            //_floating.HidePanel();
            if (_wdxc != null)
            {
                //_wdxc.Disposed -= _wdxc_Disposed;
                //_wdxc.LayoutUpdated -= _wdxc_LayoutUpdated;
                //_wdxc.KeyDown -= _wdxc_KeyDown;
                _wdxc.Close();
                _wdxc = null;
            }
        }

        private void _wdxc_KeyDown(object sender, ClowdWin64.DxKeyDownEventArgs e)
        {
            _floating?.ProcessKey(KeyInterop.KeyFromVirtualKey(e.KeyCode));
        }

        private void _wdxc_Disposed(object sender, ClowdWin64.DxDisposedEventArgs e)
        {
            DisposeInternal();
            Closed?.Invoke(this, new EventArgs());

            if (e.Error != null)
            {
                _floating?.HidePanel();
                _floating.Dispatcher.Invoke(() =>
                {
                    NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, e.Error.ToString(), "An unhandled error occurred while showing screen capture window");
                });
            }
        }

        private void _wdxc_LayoutUpdated(object sender, ClowdWin64.DxLayoutUpdatedEventArgs e)
        {
            if (e.Captured)
            {
                _floating.ShowPanel(e.Selection, IntPtr.Zero);
            }
            else
            {
                _floating.HidePanel();
            }
        }

        private void _wdxc_ColorCaptured(object sender, ClowdWin64.DxColorCapturedEventArgs e)
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
