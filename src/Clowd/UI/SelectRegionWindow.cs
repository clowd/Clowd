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
        static ClowdWin64.DXCaptureWindow _wdxc;
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

        //private static async void _floating_Activated(object sender, EventArgs e)
        //{
        //    if (_wdxc != null)
        //    {
        //        await Task.Delay(10);
        //        USER32.SetForegroundWindow(_wdxc.Handle);
        //    }
        //}

        static async void OnUpload(object sender, EventArgs e)
        {
            if (_wdxc == null) return;
            var sel = _wdxc.Selection;
            WriteableBitmap bmp = new WriteableBitmap(sel.Width, sel.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);

            bmp.Lock();
            var size = bmp.BackBufferStride * bmp.PixelHeight;
            _wdxc.WriteToPointer(bmp.BackBuffer, size);

            DisposeInternal();

            bmp.Unlock();
            bmp.Freeze();

            MemoryStream ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

            await UploadManager.UploadImage(ms, "png", viewName: "Screenshot");
        }

        static void OnPhoto(object sender, EventArgs e)
        {
            if (_wdxc == null) return;
            var sel = _wdxc.Selection;
            WriteableBitmap bmp = new WriteableBitmap(sel.Width, sel.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);

            bmp.Lock();
            var size = bmp.BackBufferStride * bmp.PixelHeight;
            _wdxc.WriteToPointer(bmp.BackBuffer, size);

            DisposeInternal();

            bmp.Unlock();
            bmp.Freeze();

            ImageEditorPage.ShowNewEditor(bmp, ScreenRect.FromSystem(sel).ToWpfRect());
        }

        static void OnVideo(object sender, EventArgs e)
        {
            if (_wdxc == null) return;
            var rect = _wdxc.Selection;
            DisposeInternal();
            var manager = App.GetService<IPageManager>();
            var video = manager.CreateVideoCapturePage();
            video.Open(ScreenRect.FromSystem(rect));
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
            var filename = await NiceDialog.ShowSelectSaveFileDialog(null, "Save Screenshot", App.Current.Settings.LastSavePath, "screenshot", "png");

            if (_wdxc != null && !String.IsNullOrWhiteSpace(filename) && Directory.Exists(Path.GetDirectoryName(filename)))
            {
                _wdxc.WriteToFile(filename);
                if (File.Exists(filename))
                {
                    Interop.Shell32.WindowsExplorer.ShowFileOrFolder(filename);
                    App.Current.Settings.LastSavePath = Path.GetDirectoryName(filename);
                }
            }

            DisposeInternal();
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
                if (_floating == null)
                {
                    throw new InvalidOperationException("Floating window does not exist. Please create it first.");
                }

                var clr = System.Drawing.Color.FromArgb(App.Current.AccentColor.A, App.Current.AccentColor.R, App.Current.AccentColor.G, App.Current.AccentColor.B);

                // create new capture
                var dx = new ClowdWin64.DXCaptureWindow(clr, true);
                dx.Disposed += _wdxc_Disposed;
                dx.LayoutUpdated += _wdxc_LayoutUpdated;
                dx.KeyDown += _wdxc_KeyDown;
                dx.Show();

                // close old capture (if any)
                DisposeInternal();

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
            }
        }

        private static void DisposeInternal()
        {
            _floating.HidePanel();
            if (_wdxc != null)
            {
                //_wdxc.Disposed -= _wdxc_Disposed;
                //_wdxc.LayoutUpdated -= _wdxc_LayoutUpdated;
                //_wdxc.KeyDown -= _wdxc_KeyDown;
                _wdxc.Dispose();
                _wdxc = null;
            }
        }

        private void _wdxc_KeyDown(object sender, ClowdWin64.CWKeyDownEventArgs e)
        {
            _floating?.ProcessKey(KeyInterop.KeyFromVirtualKey(e.KeyCode));
        }

        private void _wdxc_Disposed(object sender, EventArgs e)
        {
            DisposeInternal();
            Closed?.Invoke(this, new EventArgs());
        }

        private void _wdxc_LayoutUpdated(object sender, ClowdWin64.CWLayoutUpdatedEventArgs e)
        {
            if (_wdxc?.HasCapturedArea == true)
            {
                _floating.ShowPanel(_wdxc.Selection, _wdxc.Handle);
            }
            else
            {
                _floating.HidePanel();
            }
        }
    }
}
