using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// The capture overlay's SEARCH action (CAPTURE_PROTOCOL.md §1.2, the <c>search-image</c>
    /// marker): hands a captured image to Google Lens and leaves the browser on the results.
    /// </summary>
    /// <remarks>
    /// <para>The upload is made BY THE BROWSER, from a one-shot local HTML page that posts the
    /// image to Lens' upload endpoint and is navigated away by the response. That indirection is
    /// the whole design, and it is not optional: Lens ties an upload to the session that made it,
    /// so a POST from this process yields a results URL that reads "Expired upload" in anyone
    /// else's browser — including the user's. Posting from the browser makes the uploading session
    /// and the viewing session the same one, which is exactly what dropping a file onto
    /// lens.google.com does.</para>
    /// <para>Going to Lens directly rather than through the user's own upload provider is also
    /// deliberate: a reverse image search must not publish the screenshot to a host the user did
    /// not choose, and must work whether or not uploads are configured at all.</para>
    /// <para>The image arrives as the caller's temp copy (<see cref="CaptureResult.ImagePath"/>),
    /// moved out of a session directory that has already been deleted; it is deleted here once its
    /// bytes are in the page.</para>
    /// </remarks>
    public static class ReverseImageSearch
    {
        /// <summary>Lens' upload endpoint. <c>ep=ccm</c> ("camera capture mode") is what the
        /// clients send for a freshly taken picture, which is what a screen capture is; <c>st</c>
        /// is the client's timestamp. The separators are written <c>&amp;amp;</c> because this
        /// only ever lands in a <c>form action</c> attribute, where a bare <c>&amp;</c> is an
        /// unterminated entity.</summary>
        private const string UploadEndpoint = "https://lens.google.com/v3/upload?ep=ccm&amp;s=&amp;st=";

        /// <summary>The multipart field Lens reads the image out of.</summary>
        private const string ImageField = "encoded_image";

        /// <summary>Prefix of the throwaway pages, so one left behind by a browser that never
        /// started (or by a crash between writing and opening) can be swept on the next
        /// search.</summary>
        private const string PagePrefix = "clowd-lens-";

        /// <summary>How long a page is left on disk before the sweep treats it as abandoned. Long
        /// enough for a cold browser start on a slow machine, short enough that a capture does not
        /// sit in the temp directory for the rest of the session.</summary>
        private static readonly TimeSpan PageLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Opens a Google Lens search for <paramref name="imagePath"/> in the default browser and
        /// deletes the file. Never throws: a failure is reported to the user and swallowed, since
        /// there is nothing left of the capture to fall back to.
        /// </summary>
        public static async Task SearchAsync(string imagePath)
        {
            if (String.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                Debug.WriteLine("Reverse image search asked for a file that is not there: " + imagePath);
                return;
            }

            try
            {
                SweepAbandonedPages();

                var png = await File.ReadAllBytesAsync(imagePath);
                var pagePath = Path.Combine(Path.GetTempPath(), PagePrefix + Guid.NewGuid().ToString("N") + ".html");
                // UTF8Encoding(false): no BOM. A BOM ahead of the doctype is served as content and
                // drops the page into quirks mode.
                await File.WriteAllTextAsync(pagePath, BuildPage(png), new UTF8Encoding(false));

                // The browser reads the page after this returns, so it is NOT deleted here; the
                // sweep at the top of the next search collects it.
                Process.Start(new ProcessStartInfo(pagePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Reverse image search failed: " + ex);
                SentryConfig.CaptureHandled(ex, "capture.reverse-image-search");
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                    "The capture could not be handed to Google Lens.\n\n" + ex.Message,
                    "Reverse image search failed");
            }
            finally
            {
                TryDelete(imagePath);
            }
        }

        /// <summary>
        /// The one-shot page: it carries the capture inline, rebuilds it as a file in the
        /// browser's own memory and posts it to Lens, so the browser owns the upload session and
        /// is left on the results page the response redirects to.
        /// </summary>
        /// <remarks>
        /// The file input is filled through a <c>DataTransfer</c> — the only way a script may put
        /// bytes into one — because a plain form is the only thing that can carry a multipart body
        /// cross-origin: <c>fetch</c> would be a CORS request, and Lens answers none. Every step
        /// is guarded, since a browser without <c>DataTransfer</c> (or with scripting off) has to
        /// say so rather than sit on a blank page forever.
        /// </remarks>
        private static string BuildPage(byte[] png)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var action = UploadEndpoint + timestamp;
            var base64 = Convert.ToBase64String(png);

            return @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Reverse image search</title>
<style>
  html, body { height: 100%; margin: 0; }
  body {
    display: flex; align-items: center; justify-content: center;
    background: #1f1f1f; color: #e8e8e8;
    font: 15px/1.5 -apple-system, ""Segoe UI"", system-ui, sans-serif;
  }
  #msg { max-width: 34em; padding: 0 24px; text-align: center; }
</style>
</head>
<body>
<p id=""msg"">Sending your capture to Google Lens&hellip;</p>
<form id=""lens"" method=""POST"" enctype=""multipart/form-data"" action=""" + action + @""">
  <input type=""file"" id=""image"" name=""" + ImageField + @""" hidden>
</form>
<script>
(function () {
  var fail = function (why) {
    document.getElementById('msg').textContent =
      'This page could not start the search (' + why + '). You can drag the capture onto lens.google.com instead.';
  };
  try {
    if (typeof DataTransfer !== 'function') return fail('this browser cannot hand files to a form');
    var binary = atob('" + base64 + @"');
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    var transfer = new DataTransfer();
    transfer.items.add(new File([bytes], 'capture.png', { type: 'image/png' }));
    document.getElementById('image').files = transfer.files;
    document.getElementById('lens').submit();
  } catch (e) {
    fail(e && e.message ? e.message : e);
  }
})();
</script>
</body>
</html>
";
        }

        /// <summary>Deletes pages older than <see cref="PageLifetime"/>. Best effort throughout: a
        /// page that cannot be deleted (still open in a browser, locked by a scanner) is left for
        /// the next sweep.</summary>
        private static void SweepAbandonedPages()
        {
            try
            {
                var cutoff = DateTime.UtcNow - PageLifetime;
                foreach (var stale in Directory.EnumerateFiles(Path.GetTempPath(), PagePrefix + "*.html")
                             .Where(f => File.GetLastWriteTimeUtc(f) < cutoff)
                             .ToList())
                {
                    TryDelete(stale);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to sweep old image search pages: " + ex);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete " + path + ": " + ex);
            }
        }
    }
}
