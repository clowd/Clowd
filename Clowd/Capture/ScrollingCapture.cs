using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using Clowd.Interop;
using RT.Util.Drawing;
using ScreenVersusWpf;

namespace Clowd.Utilities
{
    public class ScrollingCapture
    {
        public Image Result { get; private set; }
        private readonly IntPtr _hWnd;
        private ScreenRect _region;
        private List<Image> _images;
        private DispatcherTimer _timer;
        private ScrollMode _mode;
        private ManualResetEvent _waitHandle;
        private int nudCombineVertical;
        private int nudCombineLastVertical;
        private int nudTrimLeft;
        private int nudTrimTop;
        private int nudTrimRight;
        private int nudTrimBottom;

        private ScrollingCapture(IntPtr hWnd)
        {
            _hWnd = hWnd;
            _images = new List<Image>();
            _timer = new DispatcherTimer();
            _timer.Tick += TimerOnTick;
            _timer.Interval = TimeSpan.FromMilliseconds(500);
        }

        public static async Task<Image> FromHandle(IntPtr hWnd)
        {
            var self = new ScrollingCapture(hWnd);
            self.StartCapture();
            await self._waitHandle.AsTask();
            return self.Result;
        }
        public static bool CanScroll(IntPtr hWnd)
        {
            var wndStyle = USER32.GetWindowLong(hWnd, WindowLongIndex.GWL_STYLE);
            bool vsVisible = (wndStyle & 0x00200000/*WS_VSCROLL*/) != 0;
            return vsVisible;
        }

        private void StartCapture()
        {
            _waitHandle = new ManualResetEvent(false);
            Task.Factory.StartNew(() =>
            {
                ScrollToTop();

                // capture some sample images to test and choose a scrolling mode.
                var b1 = ScreenUtil.Capture(_region, false);
                var mode = ScrollMode.KeyPressPageDown;
                ScrollDown(mode);
                var b2 = ScreenUtil.Capture(_region, false);

                if (IsImagesSame(b1, b2))
                {
                    ScrollToTop();
                    mode = ScrollMode.SendMessageScroll;
                    ScrollDown(mode);
                    b2 = ScreenUtil.Capture(_region, false);
                    if (IsImagesSame(b1, b2))
                    {
                        // images were the same with both scrolling mode, so could not capture.
                        Result = b1;
                        StopCapture();
                    }
                }
                _mode = mode;
                _images.Add(b1);
                _images.Add(b2);
                Thread.Sleep(200);
                _timer.IsEnabled = true;
            });
        }
        private void StopCapture()
        {
            _timer.IsEnabled = false;
            Task.Factory.StartNew(() =>
            {
                RemoveDuplicates();
                GuessEdges();
                GuessCombineAdjustments();
                var img = CombineImages();
                Result = img;
                _waitHandle.Set();
            });
        }

        private void TimerOnTick(object sender, EventArgs eventArgs)
        {
            ScrollDown(_mode);
            var source = ScreenUtil.Capture(_region, false);
            if (source != null)
                _images.Add(source);

            if (IsScrollReachedBottom(_hWnd))
                StopCapture();
        }

        private static ScrollBars GetVisibleScrollbars(IntPtr hWnd)
        {
            var wndStyle = USER32.GetWindowLong(hWnd, WindowLongIndex.GWL_STYLE);
            bool hsVisible = (wndStyle & 0x00100000/*WM_HSCROLL*/) != 0;
            bool vsVisible = (wndStyle & 0x00200000/*WS_VSCROLL*/) != 0;

            if (hsVisible)
                return vsVisible ? ScrollBars.Both : ScrollBars.Horizontal;
            else
                return vsVisible ? ScrollBars.Vertical : ScrollBars.None;
        }
        private Image CombineImages()
        {
            if (_images == null || _images.Count == 0)
            {
                return null;
            }

            if (_images.Count == 1)
            {
                return (Image)_images[0].Clone();
            }

            List<Image> output = new List<Image>();

            for (int i = 0; i < _images.Count; i++)
            {
                Image newImage;
                Image image = _images[i];

                if (nudTrimLeft > 0 || nudTrimTop > 0 || nudTrimRight > 0 || nudTrimBottom > 0 ||
                    nudCombineVertical > 0 || nudCombineLastVertical > 0)
                {
                    Rectangle rect = new Rectangle(nudTrimLeft, nudTrimTop, image.Width - nudTrimLeft - nudTrimRight,
                        image.Height - nudTrimTop - nudTrimBottom);

                    if (i == _images.Count - 1)
                    {
                        rect.Y += nudCombineLastVertical;
                        rect.Height -= nudCombineLastVertical;
                    }
                    else if (i > 0)
                    {
                        rect.Y += nudCombineVertical;
                        rect.Height -= nudCombineVertical;
                    }

                    newImage = CropImage(image, rect);

                    if (newImage == null)
                    {
                        continue;
                    }
                }
                else
                {
                    newImage = (Image)image.Clone();
                }

                output.Add(newImage);
            }

            Image result = CombineImages(output);

            foreach (Image image in output)
            {
                if (image != null)
                {
                    image.Dispose();
                }
            }

            output.Clear();

            return result;
        }
        private Image CombineImages(IEnumerable<Image> images, Orientation orientation = Orientation.Vertical, int space = 0)
        {
            int width, height;

            int spaceSize = space * (images.Count() - 1);

            if (orientation == Orientation.Vertical)
            {
                width = images.Max(x => x.Width);
                height = images.Sum(x => x.Height) + spaceSize;
            }
            else
            {
                width = images.Sum(x => x.Width) + spaceSize;
                height = images.Max(x => x.Height);
            }

            Bitmap bmp = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SetHighQuality();
                int position = 0;

                foreach (Image image in images)
                {
                    Rectangle rect;

                    if (orientation == Orientation.Vertical)
                    {
                        rect = new Rectangle(0, position, image.Width, image.Height);
                        position += image.Height + space;
                    }
                    else
                    {
                        rect = new Rectangle(position, 0, image.Width, image.Height);
                        position += image.Width + space;
                    }

                    g.DrawImage(image, rect);
                }
            }

            return bmp;
        }
        private void GuessEdges()
        {
            if (_images.Count < 2) return;

            nudTrimLeft = nudTrimTop = nudTrimRight = nudTrimBottom = 0;

            Padding result = new Padding();

            for (int i = 0; i < _images.Count - 1; i++)
            {
                Padding edges = GuessEdges(_images[i], _images[i + 1]);

                if (i == 0)
                {
                    result = edges;
                }
                else
                {
                    result.Left = Math.Min(result.Left, edges.Left);
                    result.Top = Math.Min(result.Top, edges.Top);
                    result.Right = Math.Min(result.Right, edges.Right);
                    result.Bottom = Math.Min(result.Bottom, edges.Bottom);
                }
            }

            nudTrimLeft = result.Left;
            nudTrimTop = result.Top;
            nudTrimRight = result.Right;
            nudTrimBottom = result.Bottom;
        }
        private Padding GuessEdges(Image img1, Image img2)
        {
            Padding result = new Padding();
            Rectangle rect = new Rectangle(0, 0, img1.Width, img1.Height);

            using (UnsafeBitmap bmp1 = new UnsafeBitmap((Bitmap)img1, true, ImageLockMode.ReadOnly))
            using (UnsafeBitmap bmp2 = new UnsafeBitmap((Bitmap)img2, true, ImageLockMode.ReadOnly))
            {
                bool valueFound = false;

                // Left edge
                for (int x = rect.X; !valueFound && x < rect.Width; x++)
                {
                    for (int y = rect.Y; y < rect.Height; y++)
                    {
                        if (bmp1.GetPixel(x, y) != bmp2.GetPixel(x, y))
                        {
                            valueFound = true;
                            result.Left = x;
                            rect.X = x;
                            break;
                        }
                    }
                }

                valueFound = false;

                // Top edge
                for (int y = rect.Y; !valueFound && y < rect.Height; y++)
                {
                    for (int x = rect.X; x < rect.Width; x++)
                    {
                        if (bmp1.GetPixel(x, y) != bmp2.GetPixel(x, y))
                        {
                            valueFound = true;
                            result.Top = y;
                            rect.Y = y;
                            break;
                        }
                    }
                }

                valueFound = false;

                // Right edge
                for (int x = rect.Width - 1; !valueFound && x >= rect.X; x--)
                {
                    for (int y = rect.Y; y < rect.Height; y++)
                    {
                        if (bmp1.GetPixel(x, y) != bmp2.GetPixel(x, y))
                        {
                            valueFound = true;
                            result.Right = rect.Width - x - 1;
                            rect.Width = x + 1;
                            break;
                        }
                    }
                }

                valueFound = false;

                // Bottom edge
                for (int y = rect.Height - 1; !valueFound && y >= rect.X; y--)
                {
                    for (int x = rect.X; x < rect.Width; x++)
                    {
                        if (bmp1.GetPixel(x, y) != bmp2.GetPixel(x, y))
                        {
                            valueFound = true;
                            result.Bottom = rect.Height - y - 1;
                            rect.Height = y + 1;
                            break;
                        }
                    }
                }
            }

            return result;
        }
        private void GuessCombineAdjustments()
        {
            if (_images.Count > 1)
            {
                int vertical = 0;

                for (int i = 0; i < _images.Count - 2; i++)
                {
                    int temp = CalculateVerticalOffset(_images[i], _images[i + 1]);
                    vertical = Math.Max(vertical, temp);
                }

                nudCombineVertical = vertical;
                nudCombineLastVertical = CalculateVerticalOffset(_images[_images.Count - 2], _images[_images.Count - 1]);
            }
        }
        private int CalculateVerticalOffset(Image img1, Image img2, int ignoreRightOffset = 50)
        {
            int lastMatchCount = 0;
            int lastMatchOffset = 0;

            Rectangle rect = new Rectangle(nudTrimLeft, nudTrimTop,
                img1.Width - nudTrimLeft - nudTrimRight - (img1.Width > ignoreRightOffset ? ignoreRightOffset : 0),
                img1.Height - nudTrimTop - nudTrimBottom);

            using (UnsafeBitmap bmp1 = new UnsafeBitmap((Bitmap)img1, true, ImageLockMode.ReadOnly))
            using (UnsafeBitmap bmp2 = new UnsafeBitmap((Bitmap)img2, true, ImageLockMode.ReadOnly))
            {
                for (int y = rect.Y; y < rect.Bottom; y++)
                {
                    bool isLineMatches = true;

                    for (int x = rect.X; x < rect.Right; x++)
                    {
                        if (bmp2.GetPixel(x, y) != bmp1.GetPixel(x, rect.Bottom - 1))
                        {
                            isLineMatches = false;
                            break;
                        }
                    }

                    if (isLineMatches)
                    {
                        int lineMatchesCount = 1;
                        int y3 = 2;

                        for (int y2 = y - 1; y2 >= rect.Y; y2--)
                        {
                            bool isLineMatches2 = true;

                            for (int x2 = rect.X; x2 < rect.Right; x2++)
                            {
                                if (bmp2.GetPixel(x2, y2) != bmp1.GetPixel(x2, rect.Bottom - y3))
                                {
                                    isLineMatches2 = false;
                                    break;
                                }
                            }

                            if (isLineMatches2)
                            {
                                lineMatchesCount++;
                                y3++;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (lineMatchesCount > lastMatchCount)
                        {
                            lastMatchCount = lineMatchesCount;
                            lastMatchOffset = y - rect.Y + 1;
                        }
                    }
                }
            }

            return lastMatchOffset;
        }
        private void RemoveDuplicates()
        {
            if (_images.Count > 1)
            {
                for (int i = _images.Count - 1; i > 0; i--)
                {
                    bool result = IsImagesSame((Bitmap)_images[i], (Bitmap)_images[i - 1]);

                    if (result)
                    {
                        Image img = _images[i];
                        _images.Remove(img);
                        img.Dispose();
                    }
                }
            }
        }
        private Image CropImage(Image img, Rectangle rect)
        {
            if (img != null && rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0 && new Rectangle(0, 0, img.Width, img.Height).Contains(rect))
            {
                using (Bitmap bmp = new Bitmap(img))
                {
                    return bmp.Clone(rect, bmp.PixelFormat);
                }
            }
            return null;
        }
        private void ScrollToTop()
        {
            FocusWindow();
            _region = ScreenRect.FromSystem(USER32EX.GetTrueWindowBounds(_hWnd));
            SendKeys.SendWait("{HOME}");
            SendKeys.Flush();
            USER32.SendMessage(_hWnd, (uint)WindowMessage.WM_VSCROLL, (IntPtr)6/*SB_TOP*/, (IntPtr)0);
            Thread.Sleep(100);
        }
        private void ScrollDown(ScrollMode method)
        {
            FocusWindow();
            switch (method)
            {
                case ScrollMode.SendMessageScroll:
                    USER32.SendMessage(_hWnd, (uint)WindowMessage.WM_VSCROLL, (IntPtr)3/*SB_PAGEDOWN*/, (IntPtr)0);
                    break;
                case ScrollMode.KeyPressPageDown:
                    SendKeys.SendWait("{PGDN}");
                    SendKeys.Flush();
                    break;
            }
            Thread.Sleep(100);
        }
        private void FocusWindow()
        {
            USER32.SetForegroundWindow(_hWnd);
            USER32.SetActiveWindow(_hWnd);
        }
        private bool IsScrollReachedBottom(IntPtr handle)
        {
            SCROLLINFO scrollInfo = new SCROLLINFO();
            scrollInfo.cbSize = (uint)Marshal.SizeOf(scrollInfo);
            scrollInfo.fMask = (uint)(ScrollInfoMask.SIF_RANGE | ScrollInfoMask.SIF_PAGE | ScrollInfoMask.SIF_TRACKPOS);

            if (USER32.GetScrollInfo(handle, (int)1, ref scrollInfo))
            {
                return scrollInfo.nMax == scrollInfo.nTrackPos + scrollInfo.nPage - 1;
            }

            return IsLastTwoImagesSame();
        }
        private bool IsLastTwoImagesSame()
        {
            if (_images.Count > 1)
            {
                var bmp1 = (Bitmap)_images[_images.Count - 1];
                var bmp2 = (Bitmap)_images[_images.Count - 2];
                var equals = IsImagesSame(bmp1, bmp2);
                if (equals)
                {
                    Image last = _images[_images.Count - 1];
                    _images.Remove(last);
                    last.Dispose();
                }
                return equals;
            }
            return false;
        }
        private bool IsImagesSame(Bitmap bmp1, Bitmap bmp2)
        {
            bool equals = true;

            Rectangle rect = new Rectangle(0, 0, bmp1.Width, bmp1.Height);
            BitmapData bmpData1 = bmp1.LockBits(rect, ImageLockMode.ReadOnly, bmp1.PixelFormat);
            BitmapData bmpData2 = bmp2.LockBits(rect, ImageLockMode.ReadOnly, bmp2.PixelFormat);
            try
            {
                int bitsPerPixel = ((int)bmp1.PixelFormat & 0xff00) >> 8;
                int bytesPerPixel = (bitsPerPixel + 7) / 8;
                int stride = 4 * ((rect.Width * bytesPerPixel + 3) / 4);
                unsafe
                {
                    byte* ptr1 = (byte*)bmpData1.Scan0.ToPointer();
                    byte* ptr2 = (byte*)bmpData2.Scan0.ToPointer();
                    for (int y = 0; equals && y < rect.Height; y++)
                    {
                        for (int x = 0; x < stride; x++)
                        {
                            if (*ptr1 != *ptr2)
                            {
                                equals = false;
                                break;
                            }
                            ptr1++;
                            ptr2++;
                        }
                        ptr1 += bmpData1.Stride - stride;
                        ptr2 += bmpData2.Stride - stride;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
            }
            finally
            {
                bmp1.UnlockBits(bmpData1);
                bmp2.UnlockBits(bmpData2);
            }
            Console.WriteLine(equals);
            return equals;
        }

        private enum ScrollMode
        {
            SendMessageScroll,
            KeyPressPageDown,
        }
    }
}
