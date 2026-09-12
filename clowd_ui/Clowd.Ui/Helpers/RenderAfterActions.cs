using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Clowd.UI.VideoEditor;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// What happens to a rendered video once it is on disk: the "Copy file to clipboard" and
    /// "Show in folder" boxes from the render dialog, plus the toast that says which of them ran.
    /// Shared by the out-of-process render (<c>VideoRenderManager.FinishAsync</c>) and the dev
    /// harness's in-process one, so both report a finished render in the same words.
    /// </summary>
    public static class RenderAfterActions
    {
        /// <summary>
        /// Runs the after-render actions for one render and shows the toast. Must be called on the
        /// UI thread: the toast and (off Windows) the clipboard both come from a live top-level.
        /// Nothing here can fail the render — the file is already written, so a clipboard that
        /// refuses to open only costs the user the copy, and the toast then says "Video saved".
        /// </summary>
        /// <param name="host">Window to show the toast on, or null to pick one
        /// (<see cref="PickHost"/>).</param>
        /// <param name="outputPath">The file the render wrote.</param>
        /// <param name="copyToClipboard">Put the file on the clipboard as a file-drop list.</param>
        /// <param name="showInFolder">Reveal the file in the file manager.</param>
        public static async Task RunAsync(Window host, string outputPath, bool copyToClipboard, bool showInFolder)
        {
            host ??= PickHost();

            var exists = !String.IsNullOrEmpty(outputPath) && File.Exists(outputPath);
            var copied = false;

            if (copyToClipboard && exists)
            {
                try
                {
                    // no window needed on Windows — SetClipboardFiles goes straight to CF_HDROP
                    // there and ignores both arguments, so a render that outlived its editor (or
                    // finished in a tray-only session) still copies. The other platforms need a
                    // top-level and quietly do nothing without one.
                    await ClipboardImpl.SetClipboardFiles(host?.Clipboard, host?.StorageProvider, outputPath);
                    copied = true;
                }
                catch (Exception ex)
                {
                    // another app holding the clipboard open is the usual reason, and it is not
                    // worth a dialog over: the toast quietly drops back to "Video saved".
                    Debug.WriteLine("Copying the rendered video to the clipboard failed: " + ex);
                    SentryConfig.CaptureHandled(ex, "render.copy-file");
                }
            }

            var revealed = showInFolder && exists;
            if (revealed)
                ShellHelper.RevealFileInFolder(outputPath);

            Toast.Show(host, DescribeOutcome(copied, revealed));
        }

        /// <summary>The toast text for what actually happened. "Video saved" is the floor: the
        /// render succeeded even when neither action was asked for or neither worked.</summary>
        public static string DescribeOutcome(bool copied, bool revealed)
        {
            if (copied && revealed)
                return "Copied to clipboard · shown in folder";
            if (copied)
                return "Copied to clipboard";
            if (revealed)
                return "Video saved · shown in folder";

            return "Video saved";
        }

        /// <summary>Where a render with no window of its own reports: the video editor if one is
        /// open (the user is most likely still looking at it — they pressed Render there), else
        /// whatever window <see cref="Toast.GetActiveOrMainWindow"/> finds, which is the Recents
        /// window for an auto-render or a render started from a row.</summary>
        public static Window PickHost()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var editor = desktop.Windows.OfType<VideoEditorWindow>().FirstOrDefault(w => w.IsVisible);
                if (editor != null)
                    return editor;
            }

            return Toast.GetActiveOrMainWindow();
        }
    }
}
