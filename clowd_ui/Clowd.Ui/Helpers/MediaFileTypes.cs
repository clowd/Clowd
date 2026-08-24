using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;

namespace Clowd.UI.Helpers
{
    /// <summary>The file pickers the editors open — the add-image, import-media and relink flows —
    /// share these so what the editors claim to accept is stated once. Patterns only: the pickers
    /// are opened on Windows, and every one of them also offers
    /// <see cref="FilePickerFileTypes.All"/> so an unusual container is never locked out.
    ///
    /// Drag-and-drop from Explorer/Finder has no picker to fall back on, so it tests dropped paths
    /// against these same patterns (<see cref="IsImage"/> / <see cref="IsMedia"/>) — a drop accepts
    /// exactly what the buttons beside it do.</summary>
    internal static class MediaFileTypes
    {
        /// <summary>What <c>ImageContent</c> can be composed from (SkiaSharp's codecs).</summary>
        public static FilePickerFileType Images { get; } = new FilePickerFileType("Images")
        {
            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" },
            MimeTypes = new[] { "image/png", "image/jpeg", "image/webp", "image/bmp" },
        };

        /// <summary>What can be imported as a source: video containers and bare audio files.</summary>
        public static FilePickerFileType AnyMedia { get; } = new FilePickerFileType("Video and audio")
        {
            Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.webm", "*.mp3", "*.wav", "*.m4a", "*.flac" },
            MimeTypes = new[] { "video/*", "audio/*" },
        };

        /// <summary>Bare audio files, for the audio-import button. (A video container's audio still
        /// comes in through <see cref="AnyMedia"/> — the import maps every stream either way; this
        /// filter only aims the picker at music/voiceover files.)</summary>
        public static FilePickerFileType Audio { get; } = new FilePickerFileType("Audio")
        {
            Patterns = new[] { "*.mp3", "*.wav", "*.m4a", "*.flac", "*.ogg", "*.aac" },
            MimeTypes = new[] { "audio/*" },
        };

        private static readonly HashSet<string> _imageExtensions = ExtensionsOf(Images);
        private static readonly HashSet<string> _mediaExtensions = ExtensionsOf(AnyMedia, Audio);

        /// <summary>True for a path the image flows accept (<see cref="Images"/>).</summary>
        public static bool IsImage(string path) => _imageExtensions.Contains(ExtensionOf(path));

        /// <summary>True for a path the media import accepts — a video container or a bare audio
        /// file (<see cref="AnyMedia"/> plus <see cref="Audio"/>).</summary>
        public static bool IsMedia(string path) => _mediaExtensions.Contains(ExtensionOf(path));

        private static string ExtensionOf(string path)
            => String.IsNullOrEmpty(path) ? "" : Path.GetExtension(path);

        private static HashSet<string> ExtensionsOf(params FilePickerFileType[] types)
            => types.SelectMany(t => t.Patterns)
                    .Select(p => p.TrimStart('*'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
