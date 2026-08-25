#pragma warning disable CS0618 // Type or member is obsolete
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Clowd.Clipboard;
using AvaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace Clowd;

public static class ClipboardImpl
{
    // §2.11 glue invariants: custom clipboard format carries UTF-8 JSON bytes of GraphicBase[]
    // (GraphicsSerializer); images travel as PNG bytes (decision table #51).
    //
    // Both identifiers must be spelled the way the target platform expects, because
    // DataFormat.CreateBytesPlatformFormat passes them to the OS verbatim — macOS wants a UTI,
    // everyone else wants a mime type. Getting this wrong is silent: writing "image/png" on macOS
    // publishes an NSPasteboard type literally named "image/png" that no other app looks for,
    // which is why copy appeared to do nothing there. The canvas identifier is a valid UTI on
    // every platform (macOS rejects a pasteboard item carrying a malformed type), and clipboard
    // contents are transient so there is no older-build wire format to preserve.
    private const string CANVAS_CLIPBOARD_FORMAT = "com.clowd.canvas-graphics";
    private static readonly string PNG_CLIPBOARD_FORMAT = OperatingSystem.IsMacOS() ? "public.png" : "image/png";

    // These stay "Platform" (rather than "Application") formats so the identifier reaches the OS
    // unprefixed — an Application format would prepend a per-backend app prefix and stop other
    // applications from recognizing the data.
    private static readonly DataFormat<byte[]> canvasDataFormat =
        DataFormat.CreateBytesPlatformFormat(CANVAS_CLIPBOARD_FORMAT);

    private static readonly DataFormat<byte[]> pngDataFormat =
        DataFormat.CreateBytesPlatformFormat(PNG_CLIPBOARD_FORMAT);

    // Deliberately not DataFormat.Bitmap on the write side, even though it maps to these same
    // identifiers: that format is served lazily, so the backend re-encodes the Avalonia bitmap
    // whenever the OS gets around to asking for the value. Anything that disposes the bitmap
    // before then (every caller here does) yields a zero-byte image on the clipboard. Handing
    // over bytes we encoded ourselves has no lifetime dependency. Reading is safe either way, so
    // the read path does use DataFormat.Bitmap.
    //
    // Known gap vs the Rust capture overlay: arboard writes public.tiff on macOS (it builds an
    // NSImage and calls writeObjects:), we write public.png. Every modern macOS app accepts PNG,
    // but a legacy TIFF-only app will not see the image, and an app that publishes only
    // public.tiff will not be readable here. Closing that would need native NSBitmapImageRep
    // interop, since neither Avalonia nor SkiaSharp can encode TIFF.

    [SupportedOSPlatform("windows")]
    private static readonly ClipboardFormat<byte[]> canvasFormat;

    static ClipboardImpl()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1)) {
            canvasFormat = ClipboardFormat.CreateCustomFormat(CANVAS_CLIPBOARD_FORMAT, new Clipboard.Formats.BytesDataConverter());
        }
    }

    public static async Task SetClipboardCanvasData(IClipboard clipboard, AvaBitmap bitmap, byte[] canvasData)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1)) {
            using var handle = await ClipboardGdi.OpenAsync().ConfigureAwait(false);
            ms.Position = 0;
            using var gdi = new Bitmap(ms);
            handle.SetImage(gdi);
            handle.SetFormat(canvasFormat, canvasData);
        } else {
            if (clipboard == null)
                return;

            var item = new DataTransferItem();
            item.Set(pngDataFormat, ms.ToArray());
            item.Set(canvasDataFormat, canvasData);
            using var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data).ConfigureAwait(false);
        }
    }

    public static async Task<(AvaBitmap bitmap, byte[] canvasData)> GetClipboardCanvasData(IClipboard clipboard)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1)) {
            using var handle = await ClipboardGdi.OpenAsync().ConfigureAwait(false);
            byte[] clipGraphics = null;
            if (handle.ContainsFormat(canvasFormat)) {
                clipGraphics = handle.GetFormatBytes(canvasFormat);
            }

            if (handle.ContainsImage()) {
                using var gdi = handle.GetImage();
                var bitmapData = gdi.LockBits(
                    new Rectangle(0, 0, gdi.Width, gdi.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);

                var bmp = new AvaBitmap(
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul,
                    bitmapData.Scan0,
                    new Avalonia.PixelSize(bitmapData.Width, bitmapData.Height),
                    new Avalonia.Vector(gdi.HorizontalResolution, gdi.VerticalResolution),
                    bitmapData.Stride);

                gdi.UnlockBits(bitmapData);
                return (bmp, clipGraphics);
            }

            return (null, clipGraphics);
        } else {
            if (clipboard == null)
                return (null, null);

            byte[] clipGraphics = null;
            AvaBitmap clipImage = null;

            try {
                using var data = await clipboard.TryGetDataAsync().ConfigureAwait(false);
                if (data != null) {
                    if (data.Contains(canvasDataFormat))
                        clipGraphics = await data.TryGetValueAsync(canvasDataFormat).ConfigureAwait(false);
                    // DataFormat.Bitmap rather than pngDataFormat: it resolves to the same native
                    // identifier but also decodes whatever the source app published, so images
                    // copied from other applications paste correctly.
                    if (clipGraphics == null && data.Contains(DataFormat.Bitmap))
                        clipImage = await data.TryGetValueAsync(DataFormat.Bitmap).ConfigureAwait(false);
                }
            } catch {; }

            return (clipImage, clipGraphics);
        }
    }

    public static async Task SetClipboardImage(IClipboard clipboard, byte[] pngBytes)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1)) {
            using var handle = await ClipboardGdi.OpenAsync().ConfigureAwait(false);
            using var ms = new MemoryStream(pngBytes);
            using var gdi = new Bitmap(ms);
            handle.SetImage(gdi);
        } else {
            if (clipboard == null)
                return;

            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(pngDataFormat, pngBytes));
            await clipboard.SetDataAsync(data).ConfigureAwait(false);
        }
    }

    public async static Task SetClipboardText(IClipboard clipboard, string text)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1)) {
            await ClipboardGdi.SetTextAsync(text).ConfigureAwait(false);
        } else {
            if (clipboard == null)
                return;

            await clipboard.SetTextAsync(text).ConfigureAwait(false);
        }
    }
}
