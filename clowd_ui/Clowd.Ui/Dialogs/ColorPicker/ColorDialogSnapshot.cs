using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// Hidden harness that renders the color dialog to a PNG without putting it in front of anyone:
    /// <c>clowd --color-dialog-snapshot out.png [--theme light|dark] [--tab simple|image|palette]
    /// [--initial #RRGGBB] [--viewer] [--scale 2]</c>. The window opens off-screen and never
    /// activates; its content is drawn through a RenderTargetBitmap and the process exits.
    /// </summary>
    /// <remarks>
    /// A RenderTargetBitmap sees no system backdrop, so the bars' Mica is stood in for by the
    /// theme's opaque window color for the duration of the render — which is also what the window
    /// shows wherever the compositor refuses Mica. Like the other harnesses, App.Startup calls this
    /// before the single-instance mutex, so a snapshot never forwards to a resident Clowd.
    /// <para>
    /// Trust <c>--scale 1</c> for anything drawn with a blurred BoxShadow: at other scales
    /// RenderTargetBitmap has been seen to drop a blurred shadow somewhere else on the canvas (a
    /// dark smudge in the content, even from a hidden tab) and lose the eyedropper's icon — which
    /// points at the offscreen render path rather than the dialog, though a live window at that
    /// scaling has not been checked.
    /// </para>
    /// </remarks>
    public static class ColorDialogSnapshot
    {
        // App.Startup only: it tests this constant in its harness gate, never the literal
        internal const string ArgName = "--color-dialog-snapshot";

        /// <summary>Returns true (and takes over startup) when args request a snapshot.</summary>
        public static bool TryHandleArgs(string[] args)
        {
            var path = ValueOf(args, ArgName);
            if (path == null)
                return false;

            try
            {
                Run(args, path);
            }
            catch (Exception ex)
            {
                // never Startup's error dialog: a harness must not put anything on screen
                Report(path, "snapshot failed: " + ex);
                Environment.Exit(1);
            }

            return true;
        }

        private static void Run(string[] args, string path)
        {
            Color? initial = null;
            if (ValueOf(args, "--initial") is { } hex)
                initial = Color.Parse(hex);
            else if (!HasFlag(args, "--viewer"))
                initial = Color.Parse("#00AFF0");

            var dialog = new ColorDialog(initial, !HasFlag(args, "--viewer"))
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(-10000, -10000),
            };

            dialog.RequestedThemeVariant = ValueOf(args, "--theme")?.ToLowerInvariant() switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

            dialog.ActiveTab = ValueOf(args, "--tab")?.ToLowerInvariant() switch
            {
                "image" => ColorDialogTabKind.Image,
                "palette" => ColorDialogTabKind.Palette,
                _ => ColorDialogTabKind.Simple,
            };

            // the Palette tab's preset (a HarmonyPreset name), to check each glyph and palette shape
            if (Enum.TryParse<HarmonyPreset>(ValueOf(args, "--preset"), true, out var preset))
                dialog.PaletteTab.SetPreset(preset);

            // a picture for the Image tab, to draw its loaded state (it decodes and extracts off
            // the UI thread, well inside the render delay)
            if (ValueOf(args, "--image") is { } image)
                _ = dialog.ImageTab.LoadImageFileAsync(image);

            // the Palette tab's mini flyout, open on that bar (0-4). It is drawn in the window's
            // overlay layer rather than its own popup window, which would land on a real screen,
            // so the whole window is rendered rather than just its content
            var flyout = int.TryParse(ValueOf(args, "--flyout"), out var flyoutBar) ? flyoutBar : -1;

            var scale = double.TryParse(ValueOf(args, "--scale"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;

            dialog.Show();

            if (flyout >= 0)
                DispatcherTimer.RunOnce(() => dialog.PaletteTab.OpenFlyoutForSnapshot(flyout), TimeSpan.FromMilliseconds(300));

            DispatcherTimer.RunOnce(() =>
            {
                var exitCode = 0;
                try
                {
                    var root = dialog.rootGrid;
                    if (dialog.TryFindResource("ApplicationBackgroundBrush", dialog.ActualThemeVariant, out var backdrop))
                        root.Background = backdrop as IBrush;

                    var size = root.Bounds.Size;
                    var pixels = new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
                    using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                    bitmap.Render(flyout >= 0 ? dialog : root);
                    bitmap.Save(path, PngBitmapEncoderOptions.Default);
                    Report(path, "snapshot " + path);
                }
                catch (Exception ex)
                {
                    Report(path, "snapshot failed: " + ex);
                    exitCode = 1;
                }

                Console.Out.Flush();
                Environment.Exit(exitCode);
            }, TimeSpan.FromMilliseconds(700));
        }

        /// <summary>Stdout, and a <c>.log</c> beside the PNG: a WinExe launched from a shell often
        /// has no stdout to write to, and a script still wants to know why there is no picture.</summary>
        private static void Report(string path, string line)
        {
            Console.WriteLine(line);
            try
            {
                System.IO.File.WriteAllText(path + ".log", line + Environment.NewLine);
            }
            catch
            {
                // the PNG's folder was unwritable too; stdout is all there is
            }
        }

        private static string ValueOf(string[] args, string name)
        {
            for (var i = 0; args != null && i < args.Length - 1; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static bool HasFlag(string[] args, string name) =>
            args != null && Array.Exists(args, a => String.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }
}
