using System;
using System.IO;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// What the render dialog's "Save to" box means: the full <c>.mp4</c> path a render can be
    /// pointed at, or the reason the text cannot be one. Pure string work, kept out of
    /// <see cref="RenderOptionsDialog"/> so it can be exercised without a window — a typed path is
    /// the one thing in that dialog with rules of its own.
    /// </summary>
    public static class RenderOutputPath
    {
        /// <summary>The only container the render tool writes.</summary>
        public const string Extension = ".mp4";

        /// <summary>
        /// The full path <paramref name="text"/> names, or null with the reason in
        /// <paramref name="problem"/>. A bare file name means <paramref name="defaultDirectory"/>:
        /// the box shows a full path, so a name on its own is the user replacing just the name.
        /// The extension is always <see cref="Extension"/> — typed without one (or with something
        /// else), the name still names an mp4, which is all the renderer can write.
        /// </summary>
        public static string Resolve(string text, string defaultDirectory, out string problem)
        {
            problem = null;
            text = text?.Trim();
            if (String.IsNullOrEmpty(text))
            {
                problem = "Enter where the video should be saved.";
                return null;
            }

            try
            {
                if (String.IsNullOrEmpty(Path.GetDirectoryName(text)) && !String.IsNullOrEmpty(defaultDirectory))
                    text = Path.Combine(defaultDirectory, text);

                var name = Path.GetFileName(text);
                if (String.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    problem = "This file name can't be used: " + name;
                    return null;
                }

                var full = WithExtension(Path.GetFullPath(text));
                if (String.IsNullOrEmpty(Path.GetFileNameWithoutExtension(full)))
                {
                    problem = "Enter a name for the video file.";
                    return null;
                }

                return full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // GetFullPath is the one that throws here: a path with a device name, a colon in the
                // middle, or one longer than the platform allows.
                problem = "This path can't be used: " + ex.Message;
                return null;
            }
        }

        /// <summary>The path with an <c>.mp4</c> extension, appended when it has another one or
        /// none at all.</summary>
        public static string WithExtension(string path) =>
            Extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase) ? path : path + Extension;

        /// <summary>The directory part of a path, or null when there isn't one — or the path is
        /// malformed, which it may well be: this runs on text the user is still typing.</summary>
        public static string DirectoryOf(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var dir = Path.GetDirectoryName(path);
                return String.IsNullOrEmpty(dir) ? null : dir;
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
