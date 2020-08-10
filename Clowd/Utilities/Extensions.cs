using Clowd.Interop;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpRaven;
using SharpRaven.Data;
using SharpRaven.Logging;
using ScreenVersusWpf;
using System.Windows.Controls;

namespace Clowd
{
    public static class Extensions
    {
        //http://stackoverflow.com/a/12618521/184746
        public static void RemoveRoutedEventHandlers(this UIElement element, RoutedEvent routedEvent)
        {
            // Get the EventHandlersStore instance which holds event handlers for the specified element.
            // The EventHandlersStore class is declared as internal.
            var eventHandlersStoreProperty = typeof(UIElement).GetProperty(
                "EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic);
            object eventHandlersStore = eventHandlersStoreProperty.GetValue(element, null);

            if (eventHandlersStore == null) return;

            // Invoke the GetRoutedEventHandlers method on the EventHandlersStore instance 
            // for getting an array of the subscribed event handlers.
            var getRoutedEventHandlers = eventHandlersStore.GetType().GetMethod(
                "GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var routedEventHandlers = (RoutedEventHandlerInfo[])getRoutedEventHandlers.Invoke(
                eventHandlersStore, new object[] { routedEvent });

            // Iteratively remove all routed event handlers from the element.
            foreach (var routedEventHandler in routedEventHandlers)
                element.RemoveHandler(routedEvent, routedEventHandler.Handler);
        }
        public static void MakeForeground(this Window wnd)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(wnd).Handle;
            USER32.SetForegroundWindow(handle);
        }

        private static Action EmptyDelegate = delegate () { };
        public static void DoRender(this UIElement element)
        {
            element.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, EmptyDelegate);
        }
        public static System.Windows.Forms.DialogResult ShowDialog(this System.Windows.Forms.CommonDialog dialog, Window parent)
        {
            return dialog.ShowDialog(new Wpf32Window(parent));
        }

        public static BitmapSource ToBitmapSource(this System.Drawing.Bitmap original)
        {
            IntPtr ip = original.GetHbitmap();
            BitmapSource bs = null;
            try
            {
                bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(ip,
                   IntPtr.Zero, Int32Rect.Empty,
                   System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                Interop.Gdi32.GDI32.DeleteObject(ip);
            }
            return bs;
        }
        public static BitmapSource ConvertToBitmapSourceFast(this System.Drawing.Bitmap bitmap)
        {
            var bitmapData = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);

            var bitmapSource = BitmapSource.Create(
                bitmapData.Width, bitmapData.Height,
                96, 96,
                PixelFormats.Bgr24, null,
                bitmapData.Scan0, bitmapData.Stride * bitmapData.Height, bitmapData.Stride);

            bitmap.UnlockBits(bitmapData);
            return bitmapSource;
        }

        public static System.Drawing.Bitmap Crop(this System.Drawing.Bitmap b, System.Drawing.Rectangle r)
        {
            System.Drawing.Bitmap nb = new System.Drawing.Bitmap(r.Width, r.Height);
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(nb))
            {
                g.DrawImage(b, -r.X, -r.Y);
                return nb;
            }
        }

        public static System.Drawing.Bitmap MakeGrayscale3(this System.Drawing.Bitmap original)
        {
            //create a blank bitmap the same size as original
            var newBitmap = new System.Drawing.Bitmap(original.Width, original.Height);

            //get a graphics object from the new image
            var g = System.Drawing.Graphics.FromImage(newBitmap);

            //create the grayscale ColorMatrix
            var colorMatrix = new System.Drawing.Imaging.ColorMatrix(
               new float[][]
               {
                 new float[] {.3f, .3f, .3f, 0, 0},
                 new float[] {.59f, .59f, .59f, 0, 0},
                 new float[] {.11f, .11f, .11f, 0, 0},
                 new float[] {0, 0, 0, 1, 0},
                 new float[] {0, 0, 0, 0, 1}
               });

            //create some image attributes
            var attributes = new System.Drawing.Imaging.ImageAttributes();

            //set the color matrix attribute
            attributes.SetColorMatrix(colorMatrix);

            //draw the original image on the new image
            //using the grayscale color matrix
            g.DrawImage(original, new System.Drawing.Rectangle(0, 0, original.Width, original.Height),
               0, 0, original.Width, original.Height, System.Drawing.GraphicsUnit.Pixel, attributes);

            //dispose the Graphics object
            g.Dispose();
            return newBitmap;
        }

        public static void SetPanelCanvasPositionRelativeToSelection(this StackPanel panel, WpfRect selection, int minDistance, int maxDistance, int shortEdgePx, int longEdgePx)
        {
            var selectionScreen = ScreenTools.GetScreenContaining(selection.ToScreenRect()).Bounds.ToWpfRect();
            // subtract 2 as that's the selection border width
            var bottomSpace = Math.Max(selectionScreen.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(selectionScreen.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - selectionScreen.Left, 0) - minDistance;
            double indLeft = 0, indTop = 0;

            //we want to display (and clip) the controls on/to the primary screen -
            //where the primary screen is the screen that contains the center of the cropping rectangle
            var intersecting = selectionScreen.Intersect(selection);
            if (intersecting == WpfRect.Empty)
                return; // not supposed to happen since selectionScreen contains the center of selection rect

            if (bottomSpace >= shortEdgePx)
            {
                panel.Orientation = Orientation.Horizontal;
                indLeft = intersecting.Left + intersecting.Width / 2 - longEdgePx / 2;
                indTop = Math.Min(selectionScreen.Bottom, intersecting.Bottom + maxDistance + shortEdgePx) - shortEdgePx;
            }
            else if (rightSpace >= shortEdgePx)
            {
                panel.Orientation = Orientation.Vertical;
                indLeft = Math.Min(selectionScreen.Right, intersecting.Right + maxDistance + shortEdgePx) - shortEdgePx;
                indTop = intersecting.Bottom - longEdgePx;
            }
            else if (leftSpace >= shortEdgePx)
            {
                panel.Orientation = Orientation.Vertical;
                indLeft = Math.Max(intersecting.Left - maxDistance - shortEdgePx, 0);
                indTop = intersecting.Bottom - longEdgePx;
            }
            else // inside capture rect
            {
                panel.Orientation = Orientation.Horizontal;
                indLeft = intersecting.Left + intersecting.Width / 2 - longEdgePx / 2;
                indTop = intersecting.Bottom - shortEdgePx - (maxDistance * 2);
            }

            var horizontalSize = panel.Orientation == Orientation.Horizontal ? longEdgePx : shortEdgePx;

            if (indLeft < selectionScreen.Left)
                indLeft = selectionScreen.Left;
            else if (indLeft + horizontalSize > selectionScreen.Right)
                indLeft = selectionScreen.Right - horizontalSize;

            Canvas.SetLeft(panel, indLeft);
            Canvas.SetTop(panel, indTop);
        }

        public static string ToHexRgb(this Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        public static void Save(this BitmapSource source, string filePath, ImageFormat format)
        {
            using (var ms = new MemoryStream())
            {
                source.Save(ms, format);
                File.WriteAllBytes(filePath, ms.ToArray());
            }
        }
        public static void Save(this BitmapSource source, Stream stream, ImageFormat format)
        {
            BitmapEncoder encoder;

            if (format.Equals(ImageFormat.Bmp))
                encoder = new BmpBitmapEncoder();
            else if (format.Equals(ImageFormat.Gif))
                encoder = new GifBitmapEncoder();
            else if (format.Equals(ImageFormat.Jpeg))
                encoder = new JpegBitmapEncoder();
            else if (format.Equals(ImageFormat.Tiff))
                encoder = new TiffBitmapEncoder();
            else if (format.Equals(ImageFormat.Png))
                encoder = new PngBitmapEncoder();
            else
                throw new ArgumentOutOfRangeException(nameof(format));

            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
        }

        public static bool IsOpen(this Window window)
        {
            return Application.Current.Windows.Cast<Window>().Any(x => x == window);
        }

        public class Wpf32Window : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }

            public Wpf32Window(Window wpfWindow)
            {
                Handle = new System.Windows.Interop.WindowInteropHelper(wpfWindow).Handle;
            }
        }
        public static Task AsTask(this WaitHandle handle)
        {
            return AsTask(handle, Timeout.InfiniteTimeSpan);
        }

        public static Task AsTask(this WaitHandle handle, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<object>();
            var registration = ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) =>
            {
                var localTcs = (TaskCompletionSource<object>)state;
                if (timedOut)
                    localTcs.TrySetCanceled();
                else
                    localTcs.TrySetResult(null);
            }, tcs, timeout, executeOnlyOnce: true);
            tcs.Task.ContinueWith((_, state) => ((RegisteredWaitHandle)state).Unregister(null), registration, TaskScheduler.Default);
            return tcs.Task;
        }

        public static string ToSentry(this Exception ex)
        {
            return Sentry.Default.Capture(new SentryEvent(ex));
        }

        public static string Submit(this SentryEvent ev)
        {
            return Sentry.Default.Capture(ev);
        }
    }

    public interface IExtenedRavenClient : IRavenClient
    {
        bool Enabled { get; set; }

        string SubmitLog(string message, ErrorLevel level = ErrorLevel.Info);
    }

    public static class Sentry
    {
        public static IExtenedRavenClient Default { get; private set; }

        public static void Init(string key)
        {
            Default = new MockRaven(new RavenClient(key));
        }

        private class MockRaven : IExtenedRavenClient
        {
            private readonly RavenClient _raven;

            public MockRaven(RavenClient raven)
            {
                _raven = raven;
                Enabled = true;
            }

            public void AddTrail(Breadcrumb breadcrumb)
            {
                if (this.IsDisabled())
                    return;
                ((IRavenClient)_raven).AddTrail(breadcrumb);
            }

            public string Capture(SentryEvent @event)
            {
                if (this.IsDisabled())
                    return null;
                return ((IRavenClient)_raven).Capture(@event);
            }

            public string CaptureException(Exception exception, SentryMessage message = null, ErrorLevel level = ErrorLevel.Error,
                IDictionary<string, string> tags = null, string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public string CaptureMessage(SentryMessage message, ErrorLevel level = ErrorLevel.Info, IDictionary<string, string> tags = null,
                string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public void RestartTrails()
            {
                if (this.IsDisabled())
                    return;
                ((IRavenClient)_raven).RestartTrails();
            }

            public Task<string> CaptureAsync(SentryEvent @event)
            {
                if (this.IsDisabled())
                    return Task.FromResult<string>(null);
                return ((IRavenClient)_raven).CaptureAsync(@event);
            }

            public Task<string> CaptureExceptionAsync(Exception exception, SentryMessage message = null, ErrorLevel level = ErrorLevel.Error,
                IDictionary<string, string> tags = null, string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public Task<string> CaptureMessageAsync(SentryMessage message, ErrorLevel level = ErrorLevel.Info, IDictionary<string, string> tags = null,
                string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public string CaptureEvent(Exception e)
            {
                throw new NotImplementedException();
            }

            public string CaptureEvent(Exception e, Dictionary<string, string> tags)
            {
                throw new NotImplementedException();
            }

            private bool IsDisabled()
            {
                return !this.Enabled;
            }

            public Func<Requester, Requester> BeforeSend
            {
                get { return _raven.BeforeSend; }
                set { _raven.BeforeSend = value; }
            }

            public bool Compression
            {
                get { return _raven.Compression; }
                set { _raven.Compression = value; }
            }

            public Dsn CurrentDsn
            {
                get { return _raven.CurrentDsn; }
            }

            public string Environment
            {
                get { return _raven.Environment; }
                set { _raven.Environment = value; }
            }

            public bool IgnoreBreadcrumbs
            {
                get { return _raven.IgnoreBreadcrumbs; }
                set { _raven.IgnoreBreadcrumbs = value; }
            }

            public string Logger
            {
                get { return _raven.Logger; }
                set { _raven.Logger = value; }
            }

            public IScrubber LogScrubber
            {
                get { return _raven.LogScrubber; }
                set { _raven.LogScrubber = value; }
            }

            public string Release
            {
                get { return _raven.Release; }
                set { _raven.Release = value; }
            }

            public IDictionary<string, string> Tags
            {
                get { return _raven.Tags; }
            }

            public TimeSpan Timeout
            {
                get { return _raven.Timeout; }
                set { _raven.Timeout = value; }
            }

            public bool Enabled { get; set; }
            public string SubmitLog(string message, ErrorLevel level = ErrorLevel.Info)
            {
                if (IsDisabled())
                    return null;

                var evt = new SentryEvent(message);
                evt.Level = level;
                return Capture(evt);
            }
        }
    }
}
