using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Plays an animated GIF from an <c>avares://</c> asset. Avalonia's own <see cref="Image"/>
    /// shows only the first frame, so this decodes through SkiaSharp's <see cref="SKCodec"/>,
    /// which the process already carries for the video SDK.
    ///
    /// Frames are streamed rather than decoded up front: one Skia buffer holds the frame being
    /// composed (GIF frames build on the previous one, so the prior frame is handed to the codec
    /// as the starting point), one <see cref="WriteableBitmap"/> is what gets drawn, and a
    /// <see cref="DispatcherTimer"/> paced by each frame's own delay advances them. Everything is
    /// created on attach and torn down on detach, so a GIF inside a tooltip costs nothing while
    /// the tooltip is closed and restarts from frame 0 every time it opens.
    ///
    /// A missing or unreadable asset is not an error here: the control simply draws nothing
    /// (and measures to zero), so the UI around it degrades to "no picture" instead of failing.
    /// </summary>
    public class AnimatedGifImage : Control
    {
        public static readonly StyledProperty<Uri> SourceProperty =
            AvaloniaProperty.Register<AnimatedGifImage, Uri>(nameof(Source));

        /// <summary>Assets are authored at this multiple of their logical size so they stay crisp
        /// on HiDPI; an unsized control measures to the pixel size divided by this.</summary>
        private const double AuthoredScale = 2.0;

        /// <summary>GIF delays of 0 or 1 hundredths are the legacy "as fast as possible" value,
        /// which every browser clamps to about this.</summary>
        private const int MinFrameDelayMs = 100;

        public Uri Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private SKCodec _codec;
        private SKCodecFrameInfo[] _frames;
        private SKImageInfo _info;
        private SKBitmap _buffer;
        private WriteableBitmap _bitmap;
        private DispatcherTimer _timer;
        private int _frameIndex;
        private bool _bufferHoldsFrame;

        static AnimatedGifImage()
        {
            AffectsRender<AnimatedGifImage>(SourceProperty);
            AffectsMeasure<AnimatedGifImage>(SourceProperty);
            SourceProperty.Changed.AddClassHandler<AnimatedGifImage>((c, _) => c.OnSourceChanged());
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Open();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Close();
        }

        private void OnSourceChanged()
        {
            if (!this.IsAttachedToVisualTree())
                return;

            Close();
            Open();
        }

        /// <summary>Opens the codec and shows frame 0 immediately (so the first paint is not a
        /// blank), then lets the timer carry on from there.</summary>
        private void Open()
        {
            Close();

            var source = Source;
            if (source == null)
                return;

            try
            {
                // SKCodec wants a seekable stream; the asset loader's is not guaranteed to be one
                var ms = new MemoryStream();
                using (var asset = AssetLoader.Open(source))
                    asset.CopyTo(ms);
                ms.Position = 0;

                var codec = SKCodec.Create(ms);
                if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                {
                    codec?.Dispose();
                    return;
                }

                _codec = codec;
                _frames = codec.FrameInfo ?? Array.Empty<SKCodecFrameInfo>();
                _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                _buffer = new SKBitmap(_info);
                _bitmap = new WriteableBitmap(new PixelSize(_info.Width, _info.Height), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            catch (Exception ex)
            {
                // a missing or corrupt demo asset is a blank picture, not a broken tooltip
                Debug.WriteLine("AnimatedGifImage: could not open " + source + ": " + ex.Message);
                Close();
                return;
            }

            _frameIndex = 0;
            _bufferHoldsFrame = false;
            ShowFrame(0);
            InvalidateMeasure();

            if (_frames.Length > 1)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = DelayOf(0) };
                _timer.Tick += OnTick;
                _timer.Start();
            }
        }

        private void Close()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }

            _codec?.Dispose();
            _codec = null;
            _buffer?.Dispose();
            _buffer = null;
            _bitmap?.Dispose();
            _bitmap = null;
            _frames = null;
            _scratch = null;
            _bufferHoldsFrame = false;
            _frameIndex = 0;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_codec == null || _frames == null || _frames.Length == 0)
                return;

            var next = (_frameIndex + 1) % _frames.Length;
            ShowFrame(next);
            _timer.Interval = DelayOf(next);
        }

        private TimeSpan DelayOf(int index)
        {
            var ms = _frames != null && index < _frames.Length ? _frames[index].Duration : 0;
            return TimeSpan.FromMilliseconds(ms <= 10 ? MinFrameDelayMs : ms);
        }

        /// <summary>Decodes one frame into the buffer and copies it into the drawn bitmap. The
        /// prior frame is offered as the starting point whenever the buffer still holds it (that
        /// is how GIF frames that only paint their changed region compose); frame 0, and any
        /// frame after a failed decode, starts from scratch.</summary>
        private void ShowFrame(int index)
        {
            if (_codec == null || _buffer == null || _bitmap == null)
                return;

            var prior = index > 0 && _bufferHoldsFrame && _frameIndex == index - 1 ? index - 1 : -1;
            var options = new SKCodecOptions(index, prior);
            var result = _codec.GetPixels(_info, _buffer.GetPixels(), options);
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                // try once more without a prior frame before giving up on this frame
                result = _codec.GetPixels(_info, _buffer.GetPixels(), new SKCodecOptions(index));
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    _bufferHoldsFrame = false;
                    _frameIndex = index;
                    return;
                }
            }

            _bufferHoldsFrame = true;
            _frameIndex = index;

            using (var fb = _bitmap.Lock())
            {
                var srcStride = _buffer.RowBytes;
                var dstStride = fb.RowBytes;
                var src = _buffer.GetPixels();
                if (srcStride == dstStride)
                {
                    CopyBytes(src, fb.Address, srcStride * _info.Height);
                }
                else
                {
                    var rowBytes = Math.Min(srcStride, dstStride);
                    for (var y = 0; y < _info.Height; y++)
                        CopyBytes(src + y * srcStride, fb.Address + y * dstStride, rowBytes);
                }
            }

            InvalidateVisual();
        }

        private byte[] _scratch;

        /// <summary>Native-to-native copy through a managed scratch array: the project does not
        /// allow unsafe code, and at tooltip sizes the extra hop is not worth worrying about.</summary>
        private void CopyBytes(IntPtr src, IntPtr dst, int count)
        {
            if (_scratch == null || _scratch.Length < count)
                _scratch = new byte[count];
            Marshal.Copy(src, _scratch, 0, count);
            Marshal.Copy(_scratch, 0, dst, count);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_bitmap == null)
                return new Size();

            var natural = new Size(_info.Width / AuthoredScale, _info.Height / AuthoredScale);
            // an explicit Width/Height is honored by the layout system itself, so the natural
            // size only has to be sensible when neither is set; keep the aspect when one is.
            if (!double.IsNaN(Width) && double.IsNaN(Height) && natural.Width > 0)
                return new Size(Width, Width * natural.Height / natural.Width);
            if (double.IsNaN(Width) && !double.IsNaN(Height) && natural.Height > 0)
                return new Size(Height * natural.Width / natural.Height, Height);
            return natural;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_bitmap == null)
                return;

            var bounds = Bounds.Size;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            // Uniform: fit inside the bounds keeping the aspect, centered
            var scale = Math.Min(bounds.Width / _info.Width, bounds.Height / _info.Height);
            var w = _info.Width * scale;
            var h = _info.Height * scale;
            var dest = new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
            context.DrawImage(_bitmap, new Rect(0, 0, _info.Width, _info.Height), dest);
        }
    }
}
