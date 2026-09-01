using System;
using System.IO;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// How badly a row needs its preview, expressed as a scheduler band. The values are the raw
    /// integers both work lanes order by, so a request keeps its meaning whichever lane it lands
    /// in: <see cref="Clowd.VideoSDK.Thumbs.ThumbWork"/>'s editor work sits at 10/20/30 and every
    /// value here is above it — an open editor's timeline always outranks a recents tile.
    /// A tile that has been realized but never given a viewport starts at <see cref="BufferBelow"/>:
    /// never dropped, never outranking a row the user is actually looking at.
    /// </summary>
    public enum PreviewPriority
    {
        Visible = 40,
        BufferBelow = 50,
        BufferAbove = 60,
    }

    /// <summary>
    /// What a finished preview <i>is</i>, which is what the tile needs to know to draw it: a photo
    /// is letterboxed over the checkerboard (it may be transparent, and the checkerboard is how the
    /// user sees that), an icon is drawn centred at its logical size over nothing.
    /// </summary>
    public enum PreviewKind
    {
        None,
        Photo,
        Icon,
    }

    /// <summary>
    /// What a worker decided a session's content actually is. Distinct from
    /// <see cref="PreviewKind"/>: this picks the producer and the work lane (Image, Text and Icon
    /// are cheap Lane A work, Video and Project open FFmpeg on Lane B), while
    /// <see cref="PreviewKind"/> is only about how the result is painted.
    /// </summary>
    public enum PreviewSourceKind
    {
        None,
        Image,
        Video,
        Project,

        /// <summary>A readable text payload — a pasted snippet, a source file, a log. Its first
        /// lines are typeset onto the tile, which is the only thing that tells a column of text
        /// sessions apart: they would otherwise all draw the same lettered .txt page.</summary>
        Text,

        Icon,
    }

    /// <summary>
    /// The identity of one preview. Both halves are already in memory, so building this on the UI
    /// thread costs nothing — which is the point, because the tile builds one on every layout pass
    /// that changes its band.
    ///
    /// <para>
    /// Deliberately <i>not</i> keyed on a container or on the <see cref="SessionInfo"/> instance:
    /// the Recent page destroys and recreates every container whenever any property on any session
    /// changes, so a container-keyed cache would throw away work that is still perfectly good. The
    /// session directory never gets renamed, which makes it the only stable identity a session has.
    /// </para>
    /// </summary>
    /// <param name="SessionDir">The session's directory, normalized by
    /// <see cref="NormalizeDir"/> — always go through <see cref="For"/> rather than building this
    /// by hand, or two spellings of the same directory will key two different entries.</param>
    /// <param name="StampTicks">SessionInfo.ContentModifiedUtc.Ticks. A content change mints a new
    /// key rather than mutating an old one, so a stale entry is simply never asked for again and
    /// falls out of the LRU on its own.</param>
    public readonly record struct PreviewKey(string SessionDir, long StampTicks)
    {
        /// <summary>Builds a key from a session directory and its content stamp.</summary>
        public static PreviewKey For(string sessionDir, DateTime contentModifiedUtc)
            => new PreviewKey(NormalizeDir(sessionDir), contentModifiedUtc.Ticks);

        /// <summary>
        /// The canonical spelling of a session directory: absolute, no trailing separator, and
        /// lowercased on the platforms whose filesystems are case-insensitive. Pure string work —
        /// <see cref="Path.GetFullPath(string)"/> resolves against the process's current directory
        /// and never touches disk — so this is safe on the UI thread. A path the runtime refuses to
        /// canonicalize is passed through unchanged: the key still works as an identity, it just
        /// will not match a differently spelled form of the same directory.
        /// </summary>
        public static string NormalizeDir(string sessionDir)
        {
            if (String.IsNullOrEmpty(sessionDir))
                return String.Empty;

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDir));
            }
            catch
            {
                full = sessionDir;
            }

            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? full.ToLowerInvariant()
                : full;
        }
    }

    /// <summary>
    /// Everything a worker is allowed to know about a session, captured on the UI thread by
    /// <see cref="SessionContentResolver.Snapshot"/>.
    ///
    /// <para>
    /// Workers never dereference a <see cref="SessionInfo"/>, and this immutable copy is how that
    /// is enforced structurally rather than by convention. Two reasons it has to be: SessionManager
    /// disposes a session the moment it is deleted and a disposed FileSyncObject throws from every
    /// accessor, and a producer holding a live session could set a property on it — which would
    /// fsync a file, rebuild the whole Recent page, and (through ContentModifiedUtc) re-request the
    /// very preview it was in the middle of producing.
    /// </para>
    /// </summary>
    /// <param name="SessionDir">Normalized exactly like <see cref="PreviewKey.SessionDir"/>, so the
    /// request and the key it was made for agree on the spelling.</param>
    /// <param name="FirstUploadFileName">The name the payload was uploaded under, when this session
    /// has an upload history. For a file/zip upload whose payload never lived in the session
    /// directory this and <paramref name="OriginalFileName"/> are the only surviving trace of the
    /// original extension.</param>
    /// <param name="TargetWidth">Tile size in physical pixels; producers fit their output inside it
    /// preserving aspect, so this is a bound, not the size of the result.</param>
    public sealed record PreviewRequest(
        string SessionDir,
        string PreviewImgPath,
        string VideoPath,
        string ContentKind,
        bool IsVideoProject,
        string OriginalFileName,
        string FirstUploadFileName,
        long DurationMs,
        int TargetWidth,
        int TargetHeight);

    /// <summary>
    /// What a worker resolved a <see cref="PreviewRequest"/> down to, together with the stat it
    /// took while deciding. The stamp travels with the source because it is the freshness
    /// authority — ContentModifiedUtc only nudges the engine to re-check — and because the disk
    /// cache's filename is derived from it, which is what makes that cache self-validating.
    /// </summary>
    /// <param name="Path">The file the stamp belongs to, or null for
    /// <see cref="PreviewSourceKind.Icon"/> and <see cref="PreviewSourceKind.None"/>. For a
    /// <see cref="PreviewSourceKind.Project"/> it is the composition document, not the media the
    /// composition references — the producer builds the project from the request.</param>
    /// <param name="Extension">The source file's extension, dot included and lowercased, or null
    /// when nothing on the session named one. Null means the icon has to be chosen from
    /// <see cref="PreviewRequest.ContentKind"/> instead.</param>
    /// <param name="AtTicks">Where in a video or composition to take the still from. Producers may
    /// recompute it against the real duration they probe; this is derived from the session's
    /// recorded DurationMs, which can be stale or zero.</param>
    public readonly record struct PreviewSource(
        PreviewSourceKind Kind,
        string Path,
        string Extension,
        long AtTicks,
        DateTime MtimeUtc,
        long Length);

    /// <summary>
    /// A finished preview as raw pixels: BGRA, top-down, rows tightly packed (<see cref="Stride"/>
    /// is always Width*4). Deliberately not an Avalonia Bitmap — this is produced on a worker, and
    /// Avalonia bitmap objects are only ever constructed on the UI thread, in the engine's batched
    /// drain.
    /// </summary>
    public sealed record PreviewPixels(byte[] Bgra, int Width, int Height, PreviewKind Kind)
    {
        /// <summary>Bytes per row. Producers hand over tightly packed buffers, so the engine can
        /// copy straight into a locked WriteableBitmap row by row without a per-row source offset
        /// calculation of its own.</summary>
        public int Stride => Width * 4;
    }

    /// <summary>Fixed sizes and the cache-invalidation version for the whole preview pipeline.</summary>
    public static class PreviewFormat
    {
        /// <summary>Bump to invalidate every disk-cached PNG at once: it is hashed into the cache
        /// filename, so a bump turns every existing entry into a miss and leaves the old files for
        /// the sweep to reclaim.</summary>
        public const int Version = 1;

        /// <summary>Tile size in physical pixels — 110x75 logical at 2x, matching the Recent page's
        /// preview slot.</summary>
        public const int TileWidth = 220;

        public const int TileHeight = 150;

        /// <summary>The icons8 artwork's viewBox is 48 units square; every icon is authored in that
        /// space and scaled to the target pixel size at raster time.</summary>
        public const int IconUnitPx = 48;

        /// <summary>Logical (device-independent) size the tile draws a file-type icon at, and so the
        /// size the rasterizer must reach at 1x. It is NOT <see cref="IconUnitPx"/>: the artwork's
        /// viewBox happens to be 48 units, but the tile draws it larger than that, and quantizing
        /// the raster size off the viewBox instead of the drawn size is how an icon ends up
        /// upsampled on a 100% display. Kept here rather than on the tile so the producer and the
        /// control cannot drift apart.</summary>
        public const int IconLogicalPx = 64;
    }
}
