using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The color dialog's Image tab (brief §7.1): five colors extracted from a picture, each with a
    /// ring on the picture that can be dragged to sample somewhere else. Its Selected color is the
    /// selected bar's (the first by default); before a picture is loaded it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A picture comes in by drop (anywhere on the tab), paste (Ctrl/Cmd+V while the tab is showing),
    /// Browse, or the most recent capture. It is cropped the way UniformToFill would show it in the
    /// 260 x 220 slot and shrunk to at most <see cref="SampleScale"/> times that: that crop is both
    /// what is drawn and what is sampled, so a ring's spot maps straight onto the slot and can never
    /// sit somewhere the user cannot see.
    /// </para>
    /// <para>
    /// Decoding and extraction (<see cref="ImagePalette"/>) run off the UI thread; a ring drag
    /// samples on it, which is a handful of pixels. The newest load or mood change wins: anything
    /// that finishes after a newer one started is dropped.
    /// </para>
    /// </remarks>
    public partial class ColorImageTab : ColorDialogTab
    {
        // the picture's slot (the wheel's, brief §3.3)
        private const double SlotWidth = 260;
        private const double SlotHeight = 220;

        // the crop is kept at up to this multiple of the slot, so it stays sharp on a high-DPI screen
        // and a ring reads close to the pixels the user is looking at
        private const double SampleScale = 2.5;

        // a decoded picture longer than this on either side is shrunk before anything else reads it
        private const int MaxDecodeSide = 4096;

        private const double RingSize = 22;
        private const double SelectedRingSize = 30;

        private static readonly string DropHint = OperatingSystem.IsMacOS() ? "or paste one with ⌘V" : "or paste one with Ctrl+V";
        private const string RingHint = "Drag a ring to sample a different spot. Drop or paste another image to replace it.";

        private readonly Border[] _rings = new Border[ImagePalette.ColorCount];

        private ImagePixels _pixels;
        private WriteableBitmap _display;
        private PaletteSample[] _samples;
        private PaletteMood _mood = PaletteMood.Colorful;
        private HarmonyFit _closest;

        private int _loadVersion;
        private int _extractVersion;

        private int _dragIndex = -1;
        private Vector _dragGrab;
        private PaletteSample _dragStart;

        private TopLevel _topLevel;
        private DispatcherTimer _messageTimer;

        public ColorImageTab()
        {
            InitializeComponent();
            ColorDialogTips.Attach(btnReplace);

            pasteHint.Text = DropHint;
            hintText.Text = RingHint;
            miPaste.InputGesture = new KeyGesture(Key.V, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

            bars.SelectedIndex = 0;
            bars.SelectedIndexChanged += (_, _) =>
            {
                UpdateRings();
                SyncSelection();
            };
            bars.GoToSimpleRequested += (_, color) => Host?.ShowInSimple(color);
            bars.PropertyChanged += (_, e) =>
            {
                if (e.Property == PaletteBars.HoveredIndexProperty)
                    UpdateRings();
            };

            btnBrowse.Click += async (_, _) => await BrowseAsync();
            miBrowse.Click += async (_, _) => await BrowseAsync();
            miPaste.Click += async (_, _) => await PasteAsync();

            btnClosest.Click += (_, _) =>
            {
                if (_closest != null)
                    Host?.ShowInPalette(_closest.Preset, _closest.Base);
            };

            foreach (var chip in moodChips.Children.OfType<RadioButton>())
            {
                chip.IsCheckedChanged += (_, _) =>
                {
                    if (chip.IsChecked == true && chip.Tag is PaletteMood mood)
                        SetMood(mood);
                };
            }

            moodChips.KeyDown += OnMoodKeyDown;

            for (var i = 0; i < _rings.Length; i++)
            {
                var index = i;
                var ring = new Border { Classes = { "sampleRing" }, IsVisible = false };
                ring.GotFocus += (_, _) => bars.SelectedIndex = index;
                ring.KeyDown += (_, e) => OnRingKeyDown(index, e);
                _rings[i] = ring;
                ringLayer.Children.Add(ring);
            }

            loadedState.AddHandler(PointerPressedEvent, OnPicturePressed);
            loadedState.AddHandler(PointerMovedEvent, OnPictureMoved);
            loadedState.AddHandler(PointerReleasedEvent, OnPictureReleased);
            loadedState.AddHandler(PointerCaptureLostEvent, (_, _) => _dragIndex = -1);

            AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, (_, _) => dropHighlight.IsVisible = false);
            AddHandler(DragDrop.DropEvent, OnDrop);

            ShowEmpty();
        }

        /// <summary>Loads the picture at <paramref name="path"/>, replacing any picture already
        /// here. Also the snapshot harness's way in (<c>--image</c>).</summary>
        public Task LoadImageFileAsync(string path) => LoadAsync(() => new Bitmap(path));

        /// <summary>Escape during a ring drag puts the ring back where the drag started.</summary>
        protected internal override bool HandleEscape()
        {
            if (_dragIndex < 0 || _samples == null)
                return false;

            var index = _dragIndex;
            _dragIndex = -1;
            ReplaceSample(index, _dragStart);
            return true;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // paste is the window's key, not a focused control's: after a click the dialog parks
            // focus outside every tab (ColorDialog's focus sink), so only the top level sees it
            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTopLevelKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || !IsEffectivelyVisible || e.Source is TextBox)
                return;

            var gestures = this.GetPlatformSettings()?.HotkeyConfiguration.Paste;
            var isPaste = gestures != null
                ? gestures.Any(g => g.Matches(e))
                : e.Key == Key.V && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta;
            if (!isPaste)
                return;

            e.Handled = true;
            _ = PasteAsync();
        }

        // ===== Getting a picture in =====

        private async Task BrowseAsync()
        {
            var picked = await NiceDialog.ShowSelectFilesDialog(this, "Choose an image",
                filter: new[] { MediaFileTypes.Images, FilePickerFileTypes.All });
            if (picked is { Length: > 0 })
                await LoadImageFileAsync(picked[0]);
        }

        private async Task PasteAsync()
        {
            var clipboard = _topLevel?.Clipboard;
            if (clipboard == null)
                return;

            try
            {
                // a picture first (a screenshot, an image copied in a browser), then a copied file
                var (bitmap, _) = await ClipboardImpl.GetClipboardCanvasData(clipboard);
                if (bitmap != null)
                {
                    await LoadAsync(() => bitmap);
                    return;
                }

                var files = await clipboard.TryGetFilesAsync();
                var path = files?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && MediaFileTypes.IsImage(p) && File.Exists(p));
                if (path != null)
                {
                    await LoadImageFileAsync(path);
                    return;
                }
            }
            catch (Exception)
            {
                // a clipboard owned by another app can refuse or vanish mid-read; the message below
                // covers it as well as an empty one
            }

            ShowMessage("There's no image on the clipboard.");
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            var droppable = FileDrop.GetLocalPaths(e).Any(MediaFileTypes.IsImage)
                            || e.DataTransfer?.Contains(DataFormat.Bitmap) == true;

            e.DragEffects = droppable ? DragDropEffects.Copy : DragDropEffects.None;
            dropHighlight.IsVisible = droppable;
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            dropHighlight.IsVisible = false;
            e.Handled = true;

            var path = FileDrop.GetLocalPaths(e).FirstOrDefault(MediaFileTypes.IsImage);
            if (path != null)
            {
                await LoadImageFileAsync(path);
                return;
            }

            Bitmap bitmap = null;
            try
            {
                bitmap = e.DataTransfer?.TryGetBitmap();
            }
            catch (Exception)
            {
                // the source app's data could not be decoded; reported below
            }

            if (bitmap != null)
                await LoadAsync(() => bitmap);
            else
                ShowMessage("That can't be opened as an image.");
        }

        /// <summary>
        /// Decodes (via <paramref name="open"/>, which owns nothing once it returns: the bitmap is
        /// disposed here), crops, extracts and shows a picture. Everything but the showing runs off
        /// the UI thread; a load that finishes after a newer one started is dropped.
        /// </summary>
        private async Task LoadAsync(Func<Bitmap> open)
        {
            var version = ++_loadVersion;
            var mood = _mood;

            try
            {
                var (pixels, samples, display) = await Task.Run(() =>
                {
                    using var bitmap = open();
                    var pixels = Crop(bitmap);
                    return (pixels, ImagePalette.Extract(pixels, mood), ToBitmap(pixels));
                });

                if (version != _loadVersion)
                {
                    display.Dispose();
                    return;
                }

                ShowPicture(pixels, display);
                _extractVersion++;
                ApplySamples(samples);
                bars.SelectedIndex = 0;

                // the mood changed while this was loading: catch up
                if (mood != _mood)
                    Reextract();
            }
            catch (Exception)
            {
                if (version == _loadVersion)
                    ShowMessage("That can't be opened as an image.");
            }
        }

        private void SetMood(PaletteMood mood)
        {
            if (mood == _mood)
                return;

            _mood = mood;
            Reextract();
        }

        private async void Reextract()
        {
            var pixels = _pixels;
            if (pixels == null)
                return;

            var version = ++_extractVersion;
            var mood = _mood;
            var samples = await Task.Run(() => ImagePalette.Extract(pixels, mood));
            if (version == _extractVersion && ReferenceEquals(pixels, _pixels))
                ApplySamples(samples);
        }

        /// <summary>The part of <paramref name="bitmap"/> the slot shows (UniformToFill), as plain
        /// pixels at up to <see cref="SampleScale"/> times the slot's size.</summary>
        private static ImagePixels Crop(Bitmap bitmap)
        {
            var size = bitmap.PixelSize;
            var longest = Math.Max(size.Width, size.Height);
            if (longest > MaxDecodeSide)
            {
                var scale = (double)MaxDecodeSide / longest;
                var smaller = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
                using var scaled = bitmap.CreateScaledBitmap(smaller, BitmapInterpolationMode.MediumQuality);
                return Crop(scaled);
            }

            var full = ReadPixels(bitmap);
            var (x, y, w, h) = ImagePixels.FillCrop(full.Width, full.Height, SlotWidth, SlotHeight);
            var width = Math.Max(1, (int)Math.Round(Math.Min(w, SlotWidth * SampleScale)));
            var height = Math.Max(1, (int)Math.Round(width * h / w));
            return full.Resample(x, y, w, h, width, height);
        }

        private static ImagePixels ReadPixels(Bitmap bitmap)
        {
            var size = bitmap.PixelSize;
            var bytes = new byte[size.Width * size.Height * 4];

            using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var buffer = target.Lock())
            {
                // converts into the buffer's format and alpha, whatever the source was decoded as
                bitmap.CopyPixels(buffer);
                for (var row = 0; row < size.Height; row++)
                    Marshal.Copy(buffer.Address + row * buffer.RowBytes, bytes, row * size.Width * 4, size.Width * 4);
            }

            return new ImagePixels(size.Width, size.Height, bytes);
        }

        private static WriteableBitmap ToBitmap(ImagePixels pixels)
        {
            var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var buffer = bitmap.Lock();
            for (var row = 0; row < pixels.Height; row++)
                Marshal.Copy(pixels.Bgra, row * pixels.Width * 4, buffer.Address + row * buffer.RowBytes, pixels.Width * 4);
            return bitmap;
        }

        // ===== Showing it =====

        private void ShowEmpty()
        {
            emptyState.IsVisible = true;
            loadedState.IsVisible = false;
            hintText.IsVisible = false;
            btnClosest.IsEnabled = false;
            closestName.Text = null;
            foreach (var chip in moodChips.Children)
                chip.IsEnabled = false;

            bars.Colors = null;
            SyncSelection();
        }

        private void ShowPicture(ImagePixels pixels, WriteableBitmap display)
        {
            _pixels = pixels;
            var previous = _display;
            _display = display;
            picture.Source = display;
            previous?.Dispose();

            emptyState.IsVisible = false;
            loadedState.IsVisible = true;
            hintText.IsVisible = true;
            btnClosest.IsEnabled = true;
            foreach (var chip in moodChips.Children)
                chip.IsEnabled = true;
        }

        private void ApplySamples(PaletteSample[] samples)
        {
            _samples = samples;
            var colors = samples.Select(s => s.Color).ToArray();
            bars.Colors = colors;

            _closest = ColorHarmony.FitClosest(colors);
            var name = ColorHarmony.DisplayName(_closest.Preset);
            closestName.Text = name;
            AutomationProperties.SetName(btnClosest, "To closest palette: " + name);

            UpdateRings();
            SyncSelection();
        }

        private void ReplaceSample(int index, PaletteSample sample)
        {
            var samples = (PaletteSample[])_samples.Clone();
            samples[index] = sample;
            ApplySamples(samples);
        }

        private void UpdateRings()
        {
            for (var i = 0; i < _rings.Length; i++)
            {
                var ring = _rings[i];
                ring.IsVisible = _samples != null;
                if (_samples == null)
                    continue;

                var sample = _samples[i];
                var selected = i == bars.SelectedIndex;
                var size = selected ? SelectedRingSize : RingSize;

                ring.Width = ring.Height = size;
                ring.CornerRadius = new CornerRadius(size / 2);
                ring.BorderThickness = new Thickness(selected ? 3 : 2.5);
                ring.Background = new SolidColorBrush(sample.Color);
                ring.ZIndex = selected ? 1 : 0;
                ring.Classes.Set("halo", !selected && i == bars.HoveredIndex);
                Canvas.SetLeft(ring, sample.X * SlotWidth - size / 2);
                Canvas.SetTop(ring, sample.Y * SlotHeight - size / 2);
                AutomationProperties.SetName(ring, $"Sample {i + 1}, {ColorTextHelper.GetHex(sample.Color)}");
            }
        }

        private void SyncSelection()
        {
            var colors = bars.Colors;
            var index = bars.SelectedIndex;
            SetSelectedColor(colors != null && index >= 0 && index < colors.Count ? colors[index] : null);
        }

        /// <summary>Swaps the empty state's paste line (or, once a picture is in, the hint under the
        /// chips) for <paramref name="text"/> for a few seconds.</summary>
        private void ShowMessage(string text)
        {
            if (_messageTimer == null)
            {
                _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _messageTimer.Tick += (_, _) =>
                {
                    _messageTimer.Stop();
                    pasteHint.Text = DropHint;
                    hintText.Text = RingHint;
                };
            }

            _messageTimer.Stop();
            pasteHint.Text = DropHint;
            hintText.Text = RingHint;
            if (_pixels == null)
                pasteHint.Text = text;
            else
                hintText.Text = text;
            _messageTimer.Start();
        }

        // ===== Rings =====

        /// <summary>Pressing a ring picks it up (keeping the grab offset, so it does not jump to the
        /// pointer); pressing elsewhere on the picture brings the selected ring to that spot. Either
        /// way the drag then resamples live.</summary>
        private void OnPicturePressed(object sender, PointerPressedEventArgs e)
        {
            if (_samples == null || !e.GetCurrentPoint(loadedState).Properties.IsLeftButtonPressed)
                return;

            var point = e.GetPosition(pictureHitArea);
            var index = Array.IndexOf(_rings, e.Source);
            if (index >= 0)
            {
                _dragGrab = point - new Point(_samples[index].X * SlotWidth, _samples[index].Y * SlotHeight);
            }
            else
            {
                index = Math.Max(0, bars.SelectedIndex);
                _dragGrab = default;
            }

            bars.SelectedIndex = index;
            _dragIndex = index;
            _dragStart = _samples[index];
            e.Pointer.Capture(loadedState);
            e.Handled = true;

            MoveRing(index, point - _dragGrab);
        }

        private void OnPictureMoved(object sender, PointerEventArgs e)
        {
            if (_dragIndex >= 0)
                MoveRing(_dragIndex, e.GetPosition(pictureHitArea) - _dragGrab);
        }

        private void OnPictureReleased(object sender, PointerReleasedEventArgs e)
        {
            // still captured after Escape put the ring back mid-drag
            _dragIndex = -1;
            if (ReferenceEquals(e.Pointer.Captured, loadedState))
                e.Pointer.Capture(null);
        }

        /// <summary>Puts ring <paramref name="index"/> at <paramref name="point"/> (in the slot,
        /// clamped to the picture) and reads its color there.</summary>
        private void MoveRing(int index, Point point)
        {
            var x = Math.Clamp(point.X / SlotWidth, 0, 1);
            var y = Math.Clamp(point.Y / SlotHeight, 0, 1);
            if (_samples[index].X == x && _samples[index].Y == y)
                return;

            ReplaceSample(index, ImagePalette.Sample(_pixels, x, y));
        }

        /// <summary>A focused ring moves with the arrow keys: a point per press, ten with Shift.</summary>
        private void OnRingKeyDown(int index, KeyEventArgs e)
        {
            if (_samples == null)
                return;

            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0),
                Key.Right => (step, 0),
                Key.Up => (0, -step),
                Key.Down => (0, step),
                _ => (0, 0),
            };

            if (dx == 0 && dy == 0)
                return;

            var sample = _samples[index];
            MoveRing(index, new Point(sample.X * SlotWidth + dx, sample.Y * SlotHeight + dy));
            e.Handled = true;
        }

        /// <summary>The mood chips are one radio group: the arrow keys move the choice along it.</summary>
        private void OnMoodKeyDown(object sender, KeyEventArgs e)
        {
            var chips = moodChips.Children.OfType<RadioButton>().ToArray();
            var current = Array.FindIndex(chips, c => c.IsChecked == true);
            var next = e.Key switch
            {
                Key.Left or Key.Up => (current + chips.Length - 1) % chips.Length,
                Key.Right or Key.Down => (current + 1) % chips.Length,
                Key.Home => 0,
                Key.End => chips.Length - 1,
                _ => -1,
            };

            if (next < 0 || !chips[next].IsEnabled)
                return;

            chips[next].IsChecked = true;
            chips[next].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }
}
