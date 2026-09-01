using System;
using System.IO;
using System.Threading;
using Avalonia.Threading;
using Clowd.UI.VideoEditor;
using Clowd.Upload;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// Decides what a session's preview should actually be made from. Two halves split by thread:
    /// <see cref="Snapshot"/> copies a session on the UI thread, <see cref="Resolve"/> picks the
    /// file on a worker. Nothing in between ever holds both.
    ///
    /// <para>
    /// <see cref="Resolve"/>'s <i>ordering</i> is the whole point of the preview engine. Today a
    /// GIF conversion and a video render each byte-copy the poster frame of whatever they were
    /// started from into their own new session, at a moment when the file they are producing does
    /// not exist yet — and nothing ever revisits that copy, so the entry shows a screenshot taken
    /// before its own source recording began, forever. Preferring the session's <i>content</i>
    /// (the composition, then the video, then the image) over the poster path fixes those entries
    /// without deleting a single writer: the poster stays as a fallback for the cases where it is
    /// genuinely all there is.
    /// </para>
    /// </summary>
    public static class SessionContentResolver
    {
        /// <summary>
        /// Constructing a <see cref="MimeProvider"/> deserializes a YAML language database and a
        /// JSON mime database — hundreds of milliseconds and a few MB of dictionaries. Only step 4
        /// of <see cref="Resolve"/> needs it (an upload-only session's <c>content.*</c> payload,
        /// whose extension is the only clue to what it is), and most sessions never reach step 4,
        /// so this must stay behind a Lazy and must never be touched on any other path.
        /// </summary>
        private static readonly Lazy<MimeProvider> _mime =
            new Lazy<MimeProvider>(() => new MimeProvider(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// UI THREAD. Copies out everything a worker is allowed to see. Property-bag reads only —
        /// no <c>File.Exists</c>, no probe, no enumeration. This is the firewall: the moment this
        /// returns, nothing downstream holds a reference to the session, so SessionManager is free
        /// to dispose it (which it does the instant the user deletes a row) without any in-flight
        /// preview work faulting on a disposed FileSyncObject.
        /// </summary>
        public static PreviewRequest Snapshot(SessionInfo s)
        {
#if DEBUG
            Dispatcher.UIThread.VerifyAccess();
#endif
            return new PreviewRequest(
                SessionDir: PreviewKey.NormalizeDir(Path.GetDirectoryName(s.FilePath)),
                PreviewImgPath: s.PreviewImgPath,
                VideoPath: s.VideoPath,
                ContentKind: s.ContentKind,
                IsVideoProject: s.IsVideoProject,
                OriginalFileName: s.OriginalFileName,
                FirstUploadFileName: FirstUploadFileName(s),
                DurationMs: s.DurationMs,
                TargetWidth: PreviewFormat.TileWidth,
                TargetHeight: PreviewFormat.TileHeight);
        }

        /// <summary>
        /// WORKER THREAD. Picks the source in the order documented on the class, stats the file it
        /// chose exactly once, and returns that stamp with it. Never throws: every path that can
        /// fail is a missing or unreadable file, and the answer to all of them is the same — carry
        /// on down the list, and end at an icon.
        /// </summary>
        public static PreviewSource Resolve(PreviewRequest r)
        {
            var dir = r.SessionDir;

            // 1. a video project's content IS its composition document; the media it references
            //    lives wherever the user imported it from and may not be in this directory at all.
            if (r.IsVideoProject && !String.IsNullOrEmpty(dir))
            {
                var doc = Path.Combine(dir, VideoEditPersistence.FileName);
                if (TryStat(doc, out var mtime, out var length))
                    return new PreviewSource(PreviewSourceKind.Project, doc, null,
                        PosterTicks(r.DurationMs), mtime, length);
            }

            // 2. before the poster, deliberately. This one line is what makes a recording, a render
            //    and a GIF conversion all show themselves rather than the still their session was
            //    seeded with — a .gif needs no special case, it decodes through the same avformat
            //    path every other container does.
            if (TryStat(r.VideoPath, out var videoMtime, out var videoLength))
                return new PreviewSource(PreviewSourceKind.Video, r.VideoPath,
                    ExtensionOf(r.VideoPath), PosterTicks(r.DurationMs), videoMtime, videoLength);

            // 2b. an UPLOADED image is drawn as its file-type icon, not as itself. A video upload
            //     already resolves to an icon — nothing copies the payload into the session
            //     directory and no VideoPath is set — so an image upload rendering the picture was
            //     the odd one out in a list where every other upload row is a page. This says
            //     nothing about captures: a capture carries no ContentKind at all, and its picture
            //     IS the point of the row.
            if (IsUploadedImage(r))
                return new PreviewSource(PreviewSourceKind.Icon, null,
                    UploadIconExtension(r), 0, default, 0);

            // 3. the poster path, which is the real content for a capture (cropped.png) and for the
            //    image editor's flattened output (<guid>.png).
            if (TryStat(r.PreviewImgPath, out var imgMtime, out var imgLength))
                return new PreviewSource(PreviewSourceKind.Image, r.PreviewImgPath,
                    ExtensionOf(r.PreviewImgPath), 0, imgMtime, imgLength);

            // 4. an upload-only session's payload. Two writers put one here: an image upload (whose
            //    PreviewImgPath was never set) and a text paste, which writes content.txt and gets
            //    its first lines typeset onto the tile rather than a lettered page every other text
            //    session also has.
            if (TryFindContentFile(dir, out var content, out var contentMtime, out var contentLength))
            {
                var ext = ExtensionOf(content);
                var kind = CategoryToSourceKind(ext, r.ContentKind);
                return new PreviewSource(kind, content, ext,
                    kind == PreviewSourceKind.Video ? PosterTicks(r.DurationMs) : 0,
                    contentMtime, contentLength);
            }

            // 5. the capture overlay writes cropped.png into every recording's directory too, so a
            //    recording whose mp4 has been moved or deleted still has something to show.
            if (!String.IsNullOrEmpty(dir))
            {
                var cropped = Path.Combine(dir, "cropped.png");
                if (TryStat(cropped, out var croppedMtime, out var croppedLength))
                    return new PreviewSource(PreviewSourceKind.Image, cropped, ".png", 0,
                        croppedMtime, croppedLength);
            }

            // 6. nothing on disk. An icon still has to say what the session IS, and the extension is
            //    the best available answer: the original upload name first (the only trace left of a
            //    file/zip upload, whose payload never lands in the session directory), then the name
            //    it was uploaded under, then the recording path — which is set on a video session
            //    from the moment it is created, long before the file exists.
            var iconExt = ExtensionOf(r.OriginalFileName)
                          ?? ExtensionOf(r.FirstUploadFileName)
                          ?? ExtensionOf(r.VideoPath);

            // A null extension is not a failure: the icon producer resolves it through ContentKind
            // ("image"/"video"/"text"/"file") instead, counting a project as video.
            return new PreviewSource(PreviewSourceKind.Icon, null, iconExt, 0, default, 0);
        }

        /// <summary>An image that arrived through an upload rather than through a capture. Only an
        /// upload-only session carries a ContentKind at all, so this cannot match a screenshot or an
        /// edited image, whichever files happen to be sitting in their directories.</summary>
        private static bool IsUploadedImage(PreviewRequest r)
        {
            return String.Equals(r.ContentKind, "image", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The extension an uploaded payload's icon is chosen by. Same order as step 6,
        /// minus VideoPath, which an image upload never has — and the PreviewImgPath copy last,
        /// because for an upload it is a copy of the original and carries its extension.
        /// Null is fine: the icon producer falls back to the ContentKind's own page.</summary>
        private static string UploadIconExtension(PreviewRequest r)
        {
            return ExtensionOf(r.OriginalFileName)
                   ?? ExtensionOf(r.FirstUploadFileName)
                   ?? ExtensionOf(r.PreviewImgPath);
        }

        /// <summary>
        /// The name the session's payload was uploaded under, or null. Reads
        /// <see cref="SessionInfo.AllUploads"/>, which is a computed property over the persisted
        /// upload list — in-memory, no disk.
        ///
        /// <para>
        /// UploadRecord.FileName is the real answer, but it is absent on the record AllUploads
        /// synthesizes from the legacy UploadUrl/UploadFileKey pair, and those are exactly the old
        /// sessions least likely to have anything else left to identify them by. So a URL that ends
        /// in a file name is accepted as a second choice; anything that does not is skipped rather
        /// than guessed at.
        /// </para>
        /// </summary>
        private static string FirstUploadFileName(SessionInfo s)
        {
            string fromUrl = null;

            foreach (var upload in s.AllUploads)
            {
                if (upload == null)
                    continue;

                if (!String.IsNullOrWhiteSpace(upload.FileName))
                    return upload.FileName;

                fromUrl ??= FileNameFromUrl(upload.Url);
            }

            return fromUrl;
        }

        /// <summary>The last path segment of a URL, when it looks like a file name with an
        /// extension. Plain string work on purpose — a malformed stored URL must not throw here,
        /// and Uri parsing on the UI thread to recover an extension would be absurd.</summary>
        private static string FileNameFromUrl(string url)
        {
            if (String.IsNullOrEmpty(url))
                return null;

            var end = url.IndexOfAny(new[] { '?', '#' });
            if (end >= 0)
                url = url.Substring(0, end);

            var slash = url.LastIndexOf('/');
            var name = slash >= 0 ? url.Substring(slash + 1) : url;

            var dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 ? name : null;
        }

        /// <summary>A file's existence and its stamp from a single stat. FileInfo caches the one
        /// probe it makes, so Exists / LastWriteTimeUtc / Length here are one syscall between them
        /// rather than three — and, more importantly, one consistent answer: a File.Exists followed
        /// by a separate GetLastWriteTimeUtc can straddle a file being replaced mid-render.</summary>
        private static bool TryStat(string path, out DateTime mtimeUtc, out long length)
        {
            mtimeUtc = default;
            length = 0;

            if (String.IsNullOrEmpty(path))
                return false;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return false;

                mtimeUtc = info.LastWriteTimeUtc;
                length = info.Length;
                return true;
            }
            catch
            {
                // an unreadable path, a dead network share, a name the platform rejects: all of
                // them mean "there is no source here", which the next step handles anyway.
                return false;
            }
        }

        /// <summary>The session directory's <c>content.*</c> payload. The lowest name ordinally
        /// wins rather than whatever the filesystem happens to hand back first, so a session with
        /// (say) a leftover sidecar resolves to the same file on every pass instead of flickering
        /// between two previews.</summary>
        private static bool TryFindContentFile(string dir, out string path, out DateTime mtimeUtc, out long length)
        {
            path = null;
            mtimeUtc = default;
            length = 0;

            if (String.IsNullOrEmpty(dir))
                return false;

            string best = null;
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(dir, "content.*"))
                {
                    // the pattern's trailing wildcard also matches half-written files like
                    // content.png.tmp, which are the one thing here guaranteed not to decode.
                    if (candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (best == null || String.CompareOrdinal(candidate, best) < 0)
                        best = candidate;
                }
            }
            catch
            {
                return false;
            }

            if (best == null)
                return false;

            if (!TryStat(best, out mtimeUtc, out length))
                return false;

            path = best;
            return true;
        }

        /// <summary>
        /// What an upload payload's extension says it is. An image or a video is decoded; text is
        /// typeset (a list of pasted snippets is otherwise a column of identical .txt pages, which
        /// is the one case where the file-type icon tells the user nothing at all); everything else
        /// — an archive, a document, a binary — gets an icon, because there is nothing to draw and
        /// the extension itself is the useful thing to show.
        /// </summary>
        /// <remarks>
        /// <paramref name="contentKind"/> is the session's own classification, and it is only
        /// consulted when the mime database has no opinion. That happens for extensions nobody has
        /// catalogued — and a session the app itself labelled "text" is better evidence about its
        /// own payload than an absent database row is. A payload that turns out to be binary anyway
        /// is caught by the producer, which sniffs the bytes and returns null.
        /// </remarks>
        private static PreviewSourceKind CategoryToSourceKind(string extension, string contentKind)
        {
            bool saysText = String.Equals(contentKind, "text", StringComparison.OrdinalIgnoreCase);

            if (String.IsNullOrEmpty(extension))
                return saysText ? PreviewSourceKind.Text : PreviewSourceKind.Icon;

            try
            {
                return _mime.Value.GetCategoryFromExtension(extension) switch
                {
                    ContentCategory.Image => PreviewSourceKind.Image,
                    ContentCategory.Video => PreviewSourceKind.Video,
                    ContentCategory.Text => PreviewSourceKind.Text,
                    ContentCategory.Unknown when saysText => PreviewSourceKind.Text,
                    _ => PreviewSourceKind.Icon,
                };
            }
            catch
            {
                return saysText ? PreviewSourceKind.Text : PreviewSourceKind.Icon;
            }
        }

        /// <summary>A path or bare file name's extension, dot included and lowercased, or null when
        /// there is not one. Null rather than empty so the step 6 chain can be written as a
        /// straight sequence of null coalesces.</summary>
        private static string ExtensionOf(string nameOrPath)
        {
            if (String.IsNullOrEmpty(nameOrPath))
                return null;

            string ext;
            try
            {
                ext = Path.GetExtension(nameOrPath);
            }
            catch
            {
                return null;
            }

            return String.IsNullOrEmpty(ext) || ext.Length == 1 ? null : ext.ToLowerInvariant();
        }

        /// <summary>Where to take a still from: a tenth of the way in, which skips the black or
        /// half-faded first frame most recordings and renders open on. Clamped inside the duration,
        /// and zero when the session never recorded one (a render in flight, an imported file) —
        /// producers that probe the real duration recompute this for themselves.</summary>
        private static long PosterTicks(long durationMs)
        {
            if (durationMs <= 0)
                return 0;

            var duration = durationMs * TimeSpan.TicksPerMillisecond;
            return Math.Clamp((long)(duration * 0.1), 0, duration - 1);
        }
    }
}
