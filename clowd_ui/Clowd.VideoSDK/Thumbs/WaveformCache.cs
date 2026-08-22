using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// The on-disk waveform cache: one <c>waveform-{sourceKey}-{streamIndex}.cwf</c> per analyzed
    /// stream, written beside the project's <c>videoedit.json</c>, so reopening an edit draws its
    /// audio rows immediately instead of decoding the whole recording again. The source key
    /// identifies which <i>file</i> the peaks belong to — every source in a project shares one
    /// cache directory, and stream indices are container-relative, so a recording and an imported
    /// mp4 (both with audio at stream 1) would otherwise clobber each other's caches on every open.
    ///
    /// <para>
    /// Layout (little-endian, header then <c>bucketCount</c> min/max sbyte pairs):
    /// <c>"CWF1" | u16 version | u16 bucketsPerSecond | u32 bucketCount | i64 sourceFileLength |
    /// i64 sourceMTimeUtcTicks</c>. The recording's length and modification time are the validity
    /// check — a file that was re-recorded, retranscoded or truncated in place regenerates rather
    /// than drawing peaks that belong to something else.
    /// </para>
    ///
    /// <para>
    /// Nothing here throws outward: a cache is an optimization, so a missing, truncated, corrupt or
    /// unreadable file simply means "analyze it again", and a failed write means "no cache this
    /// time". Only complete waveforms are ever written.
    /// </para>
    /// </summary>
    internal static class WaveformCache
    {
        public const int CurrentVersion = 1;

        private const int HeaderBytes = 4 + 2 + 2 + 4 + 8 + 8;
        private static readonly byte[] MagicBytes = { (byte)'C', (byte)'W', (byte)'F', (byte)'1' };

        /// <summary>The cache file name for one stream of one source. <paramref name="cacheKey"/>
        /// is the caller's stable identity for the source (the editor passes the model's
        /// <c>Source.Id</c>, which survives the session directory moving); when null the key is a
        /// hash of the path itself, so the cache still cannot collide across sources.</summary>
        public static string FileNameFor(string sourcePath, int streamIndex, string cacheKey = null) =>
            "waveform-" + (cacheKey ?? KeyForPath(sourcePath)) + "-" +
            streamIndex.ToString(CultureInfo.InvariantCulture) + ".cwf";

        /// <summary>A short stable key for a source path, compared the same way
        /// <c>WaveformProvider.StreamKeyComparer</c> compares paths: one file reached through two
        /// spellings shares a cache on Windows/macOS and does not on Linux.</summary>
        private static string KeyForPath(string sourcePath)
        {
            var normalized = OperatingSystem.IsLinux() ? sourcePath : sourcePath.ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>The cache file for a stream, or null when the caller has no directory to cache
        /// into (the dev harness opens recordings with no session directory).</summary>
        public static string PathFor(string cacheDir, string sourcePath, int streamIndex, string cacheKey = null) =>
            String.IsNullOrEmpty(cacheDir) || String.IsNullOrEmpty(sourcePath)
                ? null
                : Path.Combine(cacheDir, FileNameFor(sourcePath, streamIndex, cacheKey));

        /// <summary>The cached waveform when one is present and still describes
        /// <paramref name="sourcePath"/> at <paramref name="bucketsPerSecond"/>; null otherwise —
        /// which always means "build it".</summary>
        public static WaveformSnapshot TryLoad(string cacheDir, string sourcePath, int streamIndex,
            int bucketsPerSecond, string cacheKey = null)
        {
            try
            {
                var path = PathFor(cacheDir, sourcePath, streamIndex, cacheKey);
                if (path == null || !File.Exists(path))
                    return null;

                var source = new FileInfo(sourcePath);
                if (!source.Exists)
                    return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream);

                var magic = reader.ReadBytes(MagicBytes.Length);
                if (magic.Length != MagicBytes.Length)
                    return null;
                for (int i = 0; i < MagicBytes.Length; i++)
                {
                    if (magic[i] != MagicBytes[i])
                        return null;
                }

                if (reader.ReadUInt16() != CurrentVersion)
                    return null;
                if (reader.ReadUInt16() != bucketsPerSecond)
                    return null;

                uint bucketCount = reader.ReadUInt32();
                long sourceLength = reader.ReadInt64();
                long sourceMTimeUtcTicks = reader.ReadInt64();

                if (sourceLength != source.Length || sourceMTimeUtcTicks != source.LastWriteTimeUtc.Ticks)
                    return null;

                long pairBytes = (long)bucketCount * 2;
                if (bucketCount > Int32.MaxValue / 2 || stream.Length - HeaderBytes != pairBytes)
                    return null; // truncated (or padded) — the header and the body disagree

                var bytes = reader.ReadBytes((int)pairBytes);
                if (bytes.Length != pairBytes)
                    return null;

                var pairs = new sbyte[pairBytes];
                Buffer.BlockCopy(bytes, 0, pairs, 0, bytes.Length);
                return new WaveformSnapshot(bucketsPerSecond, pairs, (int)bucketCount, isComplete: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to read the waveform cache: " + ex.Message);
                return null;
            }
        }

        /// <summary>Writes the waveform for a stream, replacing any existing file. Returns false
        /// when there is nothing to cache (no directory, an incomplete waveform) or the write
        /// failed — never a reason to fail the analysis that produced it.</summary>
        public static bool TrySave(string cacheDir, string sourcePath, int streamIndex,
            WaveformSnapshot snapshot, string cacheKey = null)
        {
            if (snapshot == null || !snapshot.IsComplete)
                return false;

            var path = PathFor(cacheDir, sourcePath, streamIndex, cacheKey);
            if (path == null)
                return false;

            // a unique temp name, so two editors open on one session directory cannot fight over
            // (and half-delete) each other's in-flight write.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var source = new FileInfo(sourcePath);
                if (!source.Exists)
                    return false;

                Directory.CreateDirectory(cacheDir);

                int buckets = snapshot.ReadyBuckets;
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(MagicBytes);
                    writer.Write((ushort)CurrentVersion);
                    writer.Write((ushort)snapshot.BucketsPerSecond);
                    writer.Write((uint)buckets);
                    writer.Write(source.Length);
                    writer.Write(source.LastWriteTimeUtc.Ticks);

                    var bytes = new byte[buckets * 2];
                    Buffer.BlockCopy(snapshot.Pairs, 0, bytes, 0, bytes.Length);
                    writer.Write(bytes);
                }

                // one atomic replace, so a reader never sees a half-written cache
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to write the waveform cache: " + ex.Message);
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best effort
                }

                return false;
            }
        }
    }
}
