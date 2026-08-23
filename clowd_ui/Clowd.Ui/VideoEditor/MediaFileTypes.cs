using Avalonia.Platform.Storage;

namespace Clowd.UI.VideoEditor
{
    /// <summary>The file pickers the editor opens — the add-image, import-media and relink flows —
    /// share these so what the editor claims to accept is stated once. Patterns only: the pickers
    /// are opened on Windows, and every one of them also offers
    /// <see cref="FilePickerFileTypes.All"/> so an unusual container is never locked out.</summary>
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
    }
}
