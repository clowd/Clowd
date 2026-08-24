using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.Helpers
{
    /// <summary>The file pickers the editors open — the add-image, import-media and relink flows —
    /// share these so what the editors claim to accept is stated once. Patterns only: the pickers
    /// are opened on Windows, and every one of them also offers
    /// <see cref="FilePickerFileTypes.All"/> so an unusual container is never locked out.
    ///
    /// Drag-and-drop from Explorer/Finder has no picker to fall back on, so it tests dropped paths
    /// against these same patterns (<see cref="IsImage"/> / <see cref="IsMedia"/>) — a drop accepts
    /// exactly what the buttons beside it do.
    ///
    /// <c>.gif</c> is deliberately in <b>both</b> <see cref="Images"/> and <see cref="AnyMedia"/>:
    /// a GIF is a still to some people and a short silent video to others, and which one a
    /// particular file is cannot be told from its name. Every picker that takes one therefore
    /// offers it, and what it becomes is decided from the probed file — an animated GIF is
    /// imported as media so it plays, a single-frame one becomes an image item. Callers that must
    /// break the tie between <see cref="IsImage"/> and <see cref="IsMedia"/> use
    /// <see cref="IsGif"/>.</summary>
    internal static class MediaFileTypes
    {
        /// <summary>What <c>ImageContent</c> can be composed from (SkiaSharp's codecs). A GIF
        /// decodes here too — as its first frame, which is the whole of a single-frame one.</summary>
        public static FilePickerFileType Images { get; } = new FilePickerFileType("Images")
        {
            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" },
            MimeTypes = new[] { "image/png", "image/jpeg", "image/webp", "image/bmp", "image/gif" },
        };

        /// <summary>What can be imported as a source: video containers (an animated GIF among
        /// them — FFmpeg demuxes and decodes it like any other video stream) and bare audio
        /// files.</summary>
        public static FilePickerFileType AnyMedia { get; } = new FilePickerFileType("Video and audio")
        {
            Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.webm", "*.gif", "*.mp3", "*.wav", "*.m4a", "*.flac" },
            MimeTypes = new[] { "video/*", "audio/*", "image/gif" },
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

        /// <summary>True for the one extension both of the above claim — see the type remarks: a
        /// caller that must choose a flow for a GIF probes the file rather than the name.</summary>
        public static bool IsGif(string path) => ".gif".Equals(ExtensionOf(path), StringComparison.OrdinalIgnoreCase);

        /// <summary>The tie-break itself: true when the probed file is one video frame and nothing
        /// else — a GIF saved as a still, which belongs on an image track rather than as a
        /// frame-long sliver of video nobody can grab. A frame count the container does not know
        /// (0) makes no such claim, so it counts as playable material.</summary>
        public static bool IsSingleFrame(MediaProbeResult probe)
        {
            ArgumentNullException.ThrowIfNull(probe);
            return probe.AudioStreams.Count == 0
                && probe.VideoStreams.Count == 1
                && probe.VideoStreams[0].NbFrames == 1;
        }

        private static string ExtensionOf(string path)
            => String.IsNullOrEmpty(path) ? "" : Path.GetExtension(path);

        private static HashSet<string> ExtensionsOf(params FilePickerFileType[] types)
            => types.SelectMany(t => t.Patterns)
                    .Select(p => p.TrimStart('*'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
