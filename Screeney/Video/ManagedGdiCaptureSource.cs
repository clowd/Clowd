
using Clowd.Interop;
using Clowd.Interop.Gdi32;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace Screeney.Video
{
    internal class ManagedGdiCaptureSource : IVideoSource
    {
        private Rectangle _region;
        private int _frameInterval = 100;
        private int _framesReceived;

        private DateTime _lastMouseClick = DateTime.Now.AddSeconds(-5);
        private Point _lastMouseClickPosition = new Point(0, 0);
        private Thread _thread = null;
        private ManualResetEvent _stopEvent = null;

        public event NewFrameEventHandler NewFrame;
        public event VideoSourceErrorEventHandler VideoSourceError;
        public event PlayingFinishedEventHandler PlayingFinished;

        public virtual string Source
        {
            get { return "GDI Screen Capture"; }
        }
        public Rectangle Region
        {
            get { return _region; }
            set { _region = value; }
        }
        public bool Cursor { get; set; } = true;
        public int FrameInterval
        {
            get { return _frameInterval; }
            set { _frameInterval = Math.Max(0, value); }
        }
        public int FramesReceived
        {
            get
            {
                int frames = _framesReceived;
                _framesReceived = 0;
                return frames;
            }
        }

        public long BytesReceived
        {
            get { return 0; }
        }
        public bool IsRunning
        {
            get
            {
                if (_thread != null)
                {
                    if (_thread.Join(0) == false)
                        return true;
                    Free();
                }
                return false;
            }
        }
        public ManagedGdiCaptureSource(Rectangle region)
        {
            this._region = region;
        }
        public ManagedGdiCaptureSource(Rectangle region, int frameInterval)
        {
            this._region = region;
            this.FrameInterval = frameInterval;
        }
        public void Start()
        {
            if (!IsRunning)
            {
                _framesReceived = 0;
                _stopEvent = new ManualResetEvent(false);

                _thread = new Thread(new ThreadStart(WorkerThread));
                _thread.Name = Source; // mainly for debugging
                _thread.Start();
            }
        }

        public void SignalToStop()
        {
            if (_thread != null)
            {
                _stopEvent.Set();
            }
        }
        public void WaitForStop()
        {
            if (_thread != null)
            {
                _thread.Join();
                Free();
            }
        }
        public void Stop()
        {
            if (this.IsRunning)
            {
                _stopEvent.Set();
                _thread.Abort();
                WaitForStop();
            }
        }

        private void Free()
        {
            _thread = null;

            _stopEvent.Close();
            _stopEvent = null;
        }
        private void WorkerThread()
        {
            int width = _region.Width;
            int height = _region.Height;
            int x = _region.Location.X;
            int y = _region.Location.Y;
            Size size = _region.Size;

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            Graphics graphics = Graphics.FromImage(bitmap);

            DateTime start;
            TimeSpan span;

            while (!_stopEvent.WaitOne(0, false))
            {
                start = DateTime.Now;

                try
                {
                    graphics.CopyFromScreen(x, y, 0, 0, size, CopyPixelOperation.SourceCopy);
                    _framesReceived++;
                    if (NewFrame != null)
                    {
                        ProcessFrame(graphics);
                        NewFrame(this, new NewFrameEventArgs(bitmap));
                    }
                    if (_frameInterval > 0)
                    {
                        span = DateTime.Now.Subtract(start);
                        int msec = _frameInterval - (int)span.TotalMilliseconds;
                        if ((msec > 0) && (_stopEvent.WaitOne(msec, false)))
                            break;
                    }
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (VideoSourceError != null)
                    {
                        VideoSourceError(this, new VideoSourceErrorEventArgs(exception.Message));
                    }
                    Thread.Sleep(250);
                }

                if (_stopEvent.WaitOne(0, false))
                    break;
            }

            graphics.Dispose();
            bitmap.Dispose();

            if (PlayingFinished != null)
            {
                PlayingFinished(this, ReasonToFinishPlaying.StoppedByUser);
            }
        }
        private void ProcessFrame(Graphics g)
        {
            if (Cursor)
            {
                DrawCursor(g);
            }
        }
        private void DrawCursor(Graphics g)
        {
            CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (USER32.GetCursorInfo(out cursorInfo) && cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/)
            {
                var hicon = USER32.CopyIcon(cursorInfo.hCursor);
                ICONINFO iconInfo;
                int iconX, iconY;
                if (USER32.GetIconInfo(hicon, out iconInfo))
                {
                    iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot);
                    iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot);

                    if (Convert.ToBoolean(USER32.GetKeyState(VirtualKeyStates.VK_LBUTTON) & 0x8000 /*KEY_PRESSED*/) ||
                        Convert.ToBoolean(USER32.GetKeyState(VirtualKeyStates.VK_RBUTTON) & 0x8000 /*KEY_PRESSED*/))
                    {
                        _lastMouseClick = DateTime.Now;
                        _lastMouseClickPosition = new Point(cursorInfo.ptScreenPos.x, cursorInfo.ptScreenPos.y);
                    }
                    const int animationDuration = 500; //ms
                    const int animationMaxRadius = 25; //pixels
                    var lastClickSpan = Convert.ToInt32((DateTime.Now - _lastMouseClick).TotalMilliseconds);
                    if (lastClickSpan < animationDuration)
                    {
                        const int maxRadius = animationMaxRadius;
                        SolidBrush semiTransBrush = new SolidBrush(
                            Color.FromArgb((int)((1 - (lastClickSpan / (double)animationDuration)) * 255), 255, 0, 0));
                        //int radius = Math.Max(5, (int)((lastClickSpan / (double)animationDuration) * maxRadius));
                        int radius = (int)((lastClickSpan / (double)animationDuration) * maxRadius);
                        var rect = new Rectangle(_lastMouseClickPosition.X - radius, _lastMouseClickPosition.Y - radius, radius * 2, radius * 2);
                        g.FillEllipse(semiTransBrush, rect);
                        semiTransBrush.Dispose();
                    }

                    // Is this a monochrome cursor?
                    if (iconInfo.hbmColor != IntPtr.Zero)
                    {
                        using (Icon curIcon = Icon.FromHandle(hicon))
                        using (Bitmap curBitmap = curIcon.ToBitmap())
                            g.DrawImage(curBitmap, iconX, iconY);
                    }
                    else
                    {
                        //According to the ICONINFO documentation, monochrome cursors (such as I-Beam cursor):
                        //The top half of the mask bitmap is the AND mask, and the bottom half of the mask bitmap is the XOR bitmap. 
                        //When Windows draws the I-Beam cursor, the top half of this bitmap is first drawn over the desktop with an AND raster operation. 
                        //The bottom half of the bitmap is then drawn over top with an XOR raster operation. 
                        //Onscreen, The cursor will appear as the inverse of the content behind it.
                        using (Bitmap maskBitmap = Bitmap.FromHbitmap(iconInfo.hbmMask))
                        {
                            Graphics desktopGraphics = Graphics.FromHwnd(USER32.GetDesktopWindow());
                            IntPtr desktopHdc = desktopGraphics.GetHdc();
                            IntPtr maskHdc = GDI32.CreateCompatibleDC(desktopHdc);
                            IntPtr oldPtr = GDI32.SelectObject(maskHdc, maskBitmap.GetHbitmap());

                            var resultHdc = g.GetHdc();
                            var size = maskBitmap.Width;
                            GDI32.BitBlt(resultHdc, iconX, iconY, size, size, maskHdc, 0, 0, TernaryRasterOperations.SRCCOPY);
                            GDI32.BitBlt(resultHdc, iconX, iconY, size, size, maskHdc, 0, size, TernaryRasterOperations.SRCINVERT);
                            
                            g.ReleaseHdc(resultHdc);

                            IntPtr newPtr = GDI32.SelectObject(maskHdc, oldPtr);
                            GDI32.DeleteObject(newPtr);
                            GDI32.DeleteDC(maskHdc);

                            desktopGraphics.ReleaseHdc(desktopHdc);
                        }
                    }
                    USER32.DestroyIcon(hicon);
                }
            }
        }
    }
}