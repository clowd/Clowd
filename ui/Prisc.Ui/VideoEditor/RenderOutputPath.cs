using System;
using System.IO;
using Clowd.Config;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// What the render dialog's "Save to" box means: the full <c>.mp4</c> or <c>.mkv</c> path a render can be
    /// pointed at, or the reason the text cannot be one. Pure string work, kept out of
    /// <see cref="RenderOptionsDialog"/> so it can be exercised without a window — a typed path is
    /// the one thing in that dialog with rules of its own.
    /// </summary>
    public static class RenderOutputPath
    {
        /// <summary>
        /// The full path <paramref name="text"/> names, or null with the reason in
        /// <paramref name="problem"/>. A bare file name means <paramref name="defaultDirectory"/>:
        /// the box shows a full path, so a name on its own is the user replacing just the name.
        /// The extension is always <paramref name="container"/>'s — typed without one (or with
        /// something else), the name still names that container, because the extension is what
        /// the renderer picks its muxer by.
        /// </summary>
        public static string Resolve(string text, string defaultDirectory, VideoContainer container, out string problem)
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

                var full = WithExtension(Path.GetFullPath(text), container);
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

        /// <summary>The path with <paramref name="container"/>'s extension: kept when it already has
        /// it, swapped when it has the other container's (switching the dialog's Container row
        /// renames the file rather than stacking extensions), appended otherwise.</summary>
        public static string WithExtension(string path, VideoContainer container)
        {
            var extension = container.ToExtension();
            var current = ContainerOf(path);
            if (current == container)
                return path;

            return current != null ? Path.ChangeExtension(path, extension) : path + extension;
        }

        /// <summary>The container a path's extension names (<c>.mp4</c> or <c>.mkv</c>, any case),
        /// or null for any other extension or none.</summary>
        public static VideoContainer? ContainerOf(string path)
        {
            var extension = Path.GetExtension(path);
            foreach (var container in new[] { VideoContainer.Mp4, VideoContainer.Mkv })
            {
                if (container.ToExtension().Equals(extension, StringComparison.OrdinalIgnoreCase))
                    return container;
            }

            return null;
        }

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
