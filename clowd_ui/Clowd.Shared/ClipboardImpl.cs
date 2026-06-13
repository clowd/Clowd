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
    // (GraphicsSerializer); images travel as "image/png" PNG bytes (decision table #51).
    private const string CANVAS_CLIPBOARD_FORMAT = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";
    private const string PNG_CLIPBOARD_FORMAT = "image/png";

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

            var data = new DataObject();
            data.Set(PNG_CLIPBOARD_FORMAT, ms.ToArray());
            data.Set(CANVAS_CLIPBOARD_FORMAT, canvasData);
            await clipboard.SetDataObjectAsync(data).ConfigureAwait(false);
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
            byte[] clipImage = null;

            try {
                var formats = await clipboard.GetFormatsAsync().ConfigureAwait(false) ?? Array.Empty<string>();
                if (formats.Contains(CANVAS_CLIPBOARD_FORMAT))
                    clipGraphics = await clipboard.GetDataAsync(CANVAS_CLIPBOARD_FORMAT).ConfigureAwait(false) as byte[];
                if (clipGraphics == null && formats.Contains(PNG_CLIPBOARD_FORMAT))
                    clipImage = await clipboard.GetDataAsync(PNG_CLIPBOARD_FORMAT).ConfigureAwait(false) as byte[];
            } catch {; }

            if (clipImage != null) {
                using var ms = new MemoryStream(clipImage);
                return (new AvaBitmap(ms), clipGraphics);
            }

            return (null, clipGraphics);
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

            var data = new DataObject();
            data.Set(PNG_CLIPBOARD_FORMAT, pngBytes);
            await clipboard.SetDataObjectAsync(data).ConfigureAwait(false);
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
