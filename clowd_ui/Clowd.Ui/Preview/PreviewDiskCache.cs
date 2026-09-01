using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SkiaSharp;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// The on-disk preview cache: one PNG per (source file, stamp, format version), under
    /// <c>%LOCALAPPDATA%/Clowd/previews</c>. It exists for the two kinds that are genuinely
    /// expensive to produce — a video poster (an avformat open, a backward keyframe seek and up to
    /// a GOP of decoding) and a project composite (a whole <c>FrameComposer</c> pass). An image is
    /// cheaper to re-decode at sample size than to round-trip through here, and an icon rasterizes
    /// in well under a millisecond, so neither is stored.
    ///
    /// <para>
    /// <b>Worker threads only.</b> Every method here stats, enumerates, decodes or writes. The
    /// root is worse than that: <see cref="PathConstants.PreviewCache"/> <i>creates</i> its
    /// directory tree as a getter side effect, which is exactly the kind of cold-start disk work
    /// the UI thread must never do, so it is resolved once behind a <see cref="Lazy{T}"/> that the
    /// engine forces on a Lane A worker via <see cref="EnsureRoot"/>.
    /// </para>
    ///
    /// <para>
    /// <b>The filename is the whole index.</b> There is no manifest and no file header: the token
    /// hashes the resolved source path together with the source's modification time, its length
    /// and <see cref="PreviewFormat.Version"/>, so a file that changed simply hashes to a different
    /// name and is a miss rather than a stale hit that has to be detected. A superseded entry is
    /// then just an unreferenced file, which the sweep reclaims. That also means the cache needs no
    /// crash recovery — there is no state to be inconsistent with.
    /// </para>
    ///
    /// <para>
    /// Nothing here throws outward, and an unusable cache directory means "do not cache", not an
    /// error — the <c>WaveformCache.PathFor</c> convention. A preview the engine has to produce
    /// again is a slower list, not a broken one.
    /// </para>
    /// </summary>
    public static class PreviewDiskCache
    {
        /// <summary>Sweep budgets. Generous, because a single tile PNG is 5-15 KB: 200 MB is
        /// roughly ten thousand of them, and a user with that many sessions wants the hits.</summary>
        public const long SweepMaxBytes = 200L * 1024 * 1024;

        public const int SweepMaxFiles = 20_000;
        public const int SweepMaxAgeDays = 30;

        /// <summary>RFC 4648 base32, lowercased. Base32 rather than hex because the token doubles
        /// as a filename and 16 chars of base32 carry 80 bits where hex carries 64 — and unlike
        /// base64 it is case-insensitively unique, which matters on a case-insensitive filesystem.</summary>
        private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        /// <summary>10 bytes of the digest -> exactly 16 base32 characters, no padding.</summary>
        private const int TokenBytes = 10;

        private const int TokenChars = TokenBytes * 8 / 5;

        /// <summary>Null once resolution has failed — see <see cref="ResolveRoot"/>.</summary>
        private static readonly Lazy<string> _root =
            new Lazy<string>(ResolveRoot, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Forces the one-time root resolution (which creates directories) onto the
        /// calling worker. The engine calls this from its first Lane A item so that no later call
        /// can accidentally be the first one and pay for it somewhere worse.</summary>
        public static void EnsureRoot()
        {
            _ = _root.Value;
        }

        /// <summary>The cache file name stem for a resolved source, or null when the source cannot
        /// be addressed. The stamp is folded into the name rather than checked after opening, so a
        /// re-recorded or re-rendered file is a miss by construction.
        ///
        /// <para>
        /// <see cref="PreviewFormat.Version"/> is part of the material, so it must be bumped for any
        /// change that alters the produced pixels — including the tile dimensions, which are not
        /// hashed separately.
        /// </para></summary>
        public static string TokenFor(in PreviewSource src)
        {
            if (src.Kind == PreviewSourceKind.None || String.IsNullOrEmpty(src.Path))
                return null;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(src.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            // compared the way WaveformCache.KeyForPath compares paths: one file reached through
            // two spellings shares a cache entry on Windows/macOS and does not on Linux.
            if (!OperatingSystem.IsLinux())
                resolved = resolved.ToLowerInvariant();

            var material = resolved
                + "|" + src.MtimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + "|" + src.Length.ToString(CultureInfo.InvariantCulture)
                + "|" + PreviewFormat.Version.ToString(CultureInfo.InvariantCulture);

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Base32Lower(digest, TokenBytes);
        }

        /// <summary>The cached pixels for a token, or false — which always means "produce it".
        /// The kind is not stored because it cannot vary: only <see cref="PreviewSourceKind.Video"/>
        /// and <see cref="PreviewSourceKind.Project"/> reach this cache and both are
        /// <see cref="PreviewKind.Photo"/>. That is what lets the file be a plain PNG with no
        /// header of ours in front of it.</summary>
        public static bool TryLoad(string token, out PreviewPixels px)
        {
            px = null;

            var path = PathFor(token);
            if (path == null)
                return false;

            try
            {
                if (!File.Exists(path))
                    return false;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var codec = SKCodec.Create(stream);
                if (codec == null)
                    return false;

                int width = codec.Info.Width, height = codec.Info.Height;
                if (width <= 0 || height <= 0 || width > MaxCachedEdge || height > MaxCachedEdge)
                    return false; // not something we wrote; treat it as garbage the sweep will take

                // Unpremul because that is what the engine hands to WriteableBitmap
                // (AlphaFormat.Unpremul, §8) and PNG stores unpremultiplied anyway.
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var bmp = new SKBitmap(info);

                var result = codec.GetPixels(info, bmp.GetPixels());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    return false;

                int rowBytes = bmp.RowBytes, packed = width * 4;
                var pixels = bmp.GetPixelSpan();
                if (rowBytes < packed || pixels.Length < (long)rowBytes * height)
                    return false;

                // Skia is free to pad rows; PreviewPixels is tightly packed, so copy row by row
                // rather than assuming they agree.
                var bgra = new byte[(long)packed * height];
                for (int y = 0; y < height; y++)
                    pixels.Slice(y * rowBytes, packed).CopyTo(bgra.AsSpan(y * packed, packed));

                px = new PreviewPixels(bgra, width, height, PreviewKind.Photo);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to read a cached preview: " + ex.Message);
                return false;
            }
        }

        /// <summary>Writes the preview for a token, replacing whatever was there. Silent on every
        /// failure: a cache that cannot be written is not a reason to fail the work that produced
        /// the pixels.</summary>
        public static void Store(string token, PreviewPixels px)
        {
            if (px == null || px.Bgra == null || px.Width <= 0 || px.Height <= 0)
                return;

            if (px.Width > MaxCachedEdge || px.Height > MaxCachedEdge)
                return;

            var path = PathFor(token);
            if (path == null)
                return;

            var info = new SKImageInfo(px.Width, px.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            if (px.Bgra.Length < info.BytesSize)
                return;

            var temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                using (var bmp = new SKBitmap(info))
                {
                    Marshal.Copy(px.Bgra, 0, bmp.GetPixels(), info.BytesSize);

                    using var image = SKImage.FromBitmap(bmp);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (data == null)
                        return;

                    using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var encoded = data.AsStream();
                    encoded.CopyTo(file);
                }

                // one atomic replace, so a concurrent reader never opens a half-written PNG
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to write a cached preview: " + ex.Message);
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best effort; a leaked .tmp is swept like any other file
                }
            }
        }

        /// <summary>Intentionally a no-op on disk. Entries are addressed by a hash of the
        /// <i>source file</i>, with no reverse index from session directory to token, so there is
        /// nothing here to look up — and building one would mean carrying exactly the manifest this
        /// layout was designed to avoid. A deleted session's entries are simply unreferenced, and
        /// <see cref="Sweep"/> reclaims them like anything else. The visible half of a purge is
        /// <c>PreviewMemoryCache.PurgeSessionDir</c>, which the engine calls in the same breath.</summary>
        public static void PurgeSessionDir(string dir)
        {
        }

        /// <summary>Enforces the cache budgets, oldest first. Run once per process on a Lane A
        /// worker at the lowest band, 30 seconds after the engine starts, so it never competes with
        /// the previews the user is waiting on.
        ///
        /// <para>
        /// Modification time is write time here, not access time — nothing touches an entry on a
        /// hit — so the 30-day rule expires by age rather than by disuse. That is the cheap
        /// reading: it costs an occasional re-decode for a session opened every day for a month,
        /// and it costs no writes at all on the read path.
        /// </para></summary>
        public static void Sweep(CancellationToken ct)
        {
            var root = _root.Value;
            if (String.IsNullOrEmpty(root))
                return;

            try
            {
                var entries = new List<(string Path, DateTime Mtime, long Length)>();
                long totalBytes = 0;

                // the FileInfo yielded by the enumeration carries the metadata the directory scan
                // already read, so this does not stat every file a second time.
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested)
                        return;

                    try
                    {
                        entries.Add((file.FullName, file.LastWriteTimeUtc, file.Length));
                        totalBytes += file.Length;
                    }
                    catch (IOException)
                    {
                        // vanished between the scan and the read; not ours to worry about
                    }
                }

                entries.Sort(static (a, b) => a.Mtime.CompareTo(b.Mtime));

                var cutoff = DateTime.UtcNow.AddDays(-SweepMaxAgeDays);
                int totalFiles = entries.Count;

                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    // sorted oldest-first, so the moment an entry is both young enough and inside
                    // both budgets, every entry after it is too.
                    if (entry.Mtime >= cutoff && totalBytes <= SweepMaxBytes && totalFiles <= SweepMaxFiles)
                        break;

                    try
                    {
                        File.Delete(entry.Path);
                        totalBytes -= entry.Length;
                        totalFiles--;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // a shard being read by another Clowd process; leave it for next time
                    }
                }

                PruneEmptyShards(root, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Preview cache sweep failed: " + ex.Message);
            }
        }

        /// <summary>Removes shard directories the sweep emptied, so a long-lived install does not
        /// accumulate 1024 empty folders.</summary>
        private static void PruneEmptyShards(string root, CancellationToken ct)
        {
            foreach (var shard in Directory.EnumerateDirectories(root))
            {
                if (ct.IsCancellationRequested)
                    return;

                try
                {
                    using (var any = Directory.EnumerateFileSystemEntries(shard).GetEnumerator())
                    {
                        if (any.MoveNext())
                            continue;
                    }

                    Directory.Delete(shard);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // raced with a Store() that just created it; harmless
                }
            }
        }

        /// <summary>The file a token lives in, or null when there is nowhere to cache — an
        /// unresolvable root, or a token that did not come from <see cref="TokenFor"/>. Callers
        /// treat null as "skip the cache", never as a failure.</summary>
        private static string PathFor(string token)
        {
            var root = _root.Value;
            if (String.IsNullOrEmpty(root) || !IsToken(token))
                return null;

            // 2 base32 characters = a 1024-way shard, which keeps each directory to a few dozen
            // entries at the 20 000-file budget. Large flat directories are slow to enumerate on
            // NTFS and slower still on a network-redirected LOCALAPPDATA.
            return Path.Combine(root, token.Substring(0, 2), token + ".png");
        }

        /// <summary>Guards the one place a caller-supplied string becomes a path. Tokens are ours,
        /// but they are derived from extensions and file names that came off disk, so validate
        /// rather than assume.</summary>
        private static bool IsToken(string token)
        {
            if (token == null || token.Length != TokenChars)
                return false;

            foreach (var c in token)
            {
                if (Base32Alphabet.IndexOf(c) < 0)
                    return false;
            }

            return true;
        }

        private static string Base32Lower(byte[] bytes, int byteCount)
        {
            var chars = new char[byteCount * 8 / 5];
            int accumulator = 0, bits = 0, o = 0;

            for (int i = 0; i < byteCount; i++)
            {
                accumulator = (accumulator << 8) | bytes[i];
                bits += 8;

                while (bits >= 5)
                {
                    bits -= 5;
                    chars[o++] = Base32Alphabet[(accumulator >> bits) & 0x1F];
                }
            }

            return new string(chars);
        }

        /// <summary>A sanity bound on what this cache will decode or store. Everything it holds is
        /// tile-sized; anything larger is a foreign file that landed in the directory, and decoding
        /// it would allocate against a budget it was never charged to.</summary>
        private const int MaxCachedEdge = 1024;

        /// <summary>Resolves the cache root exactly once. The getter creates the directory tree, so
        /// this is the call the whole class is arranged around keeping off the UI thread. A failure
        /// (a read-only profile, a redirected LOCALAPPDATA that is offline) is permanent and means
        /// "do not cache" — it is never retried, because retrying a broken profile on every preview
        /// would cost more than the cache saves.</summary>
        private static string ResolveRoot()
        {
            try
            {
                return PathConstants.PreviewCache;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Preview cache is unavailable, previews will not be cached: " + ex.Message);
                return null;
            }
        }
    }
}
