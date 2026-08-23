using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Clowd.VideoSDK
{
    /// <summary>
    /// Loads the FFmpeg dylibs and the siblings they link against, by absolute path, before the
    /// bindings go looking for them. macOS only; a no-op everywhere else.
    ///
    /// <para>
    /// The obs-express payload is linked the way a self-contained app bundle is: every dylib in it
    /// has an <c>@rpath</c> install name and asks for its siblings the same way — libavformat wants
    /// <c>@rpath/libavcodec.dylib</c>, <c>@rpath/libx264.dylib</c>, <c>@rpath/libsrt.dylib</c> and
    /// so on. The only <c>LC_RPATH</c> in the whole payload is on the <c>obs-express</c> executable
    /// itself (<c>@executable_path/Frameworks</c>); not one of the dylibs carries one. That is fine
    /// for obs-express, and useless to us: when a *different* program dlopens one of those dylibs by
    /// absolute path, dyld expands <c>@rpath</c> against the load path of the images doing the
    /// loading, finds no rpath anywhere in that chain, and fails. FFmpeg.AutoGen reports the whole
    /// thing as "Specified method is not supported", which says nothing at all.
    /// </para>
    /// <para>
    /// <c>DYLD_LIBRARY_PATH</c> is not the answer — dyld reads it at exec, so setting it in-process
    /// is too late, and the hardened runtime strips it from a signed app anyway. Re-linking the
    /// payload with <c>install_name_tool -add_rpath</c> is not either: it lives in another
    /// repository and every dylib would have to be re-signed to survive notarization.
    /// </para>
    /// <para>
    /// What does work is loading them ourselves, first, by a path dyld does not have to resolve. An
    /// image is registered in the process under its own install name, so a later reference to
    /// <c>@rpath/libavcodec.dylib</c> matches something already loaded and is never path-resolved
    /// at all. This also fixes the out-of-process render host for free.
    /// </para>
    /// <para>
    /// Only the closure over FFmpeg's own dependencies is loaded, never the whole directory: it
    /// also holds OBS's runtime and graphics backends, and its own copy of FreeType, none of which
    /// has any business in a process that already has Skia's. The closure is read out of each
    /// file's <c>LC_LOAD_DYLIB</c> commands rather than written down here, so a payload that picks
    /// up a new codec dependency keeps working without anyone remembering to edit this list.
    /// </para>
    /// </summary>
    internal static class MacDylibPreloader
    {
        /// <summary>The libraries FFmpeg.AutoGen itself opens — the roots of the closure. Named
        /// unversioned, which is how their install names read and therefore how their dependents
        /// ask for them; the directory holds the same code under all three of
        /// <c>libavcodec.dylib</c>, <c>libavcodec.61.dylib</c> and <c>libavcodec.61.19.101.dylib</c>
        /// (the zip materializes what were symlinks as full copies), and loading more than one of
        /// them would put the same library in the process twice.</summary>
        private static readonly string[] Roots =
        {
            "libavutil.dylib", "libswresample.dylib", "libswscale.dylib", "libpostproc.dylib",
            "libavcodec.dylib", "libavformat.dylib", "libavfilter.dylib", "libavdevice.dylib",
        };

        /// <summary>
        /// Preloads what <paramref name="directory"/> holds. Best-effort throughout: a library that
        /// will not load is left for dyld to fail on later with its own message, because this is an
        /// optimization of *when* loading happens, not a precondition — a payload whose dylibs
        /// resolve on their own (a system FFmpeg, say) never needed any of this.
        /// </summary>
        public static void Preload(string directory)
        {
            if (!OperatingSystem.IsMacOS() || String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            var closure = BuildClosure(directory);

            // Order is not computed, only converged on: load what will load, and go round again if
            // anything did, because a failure here means a dependency that a later pass supplies.
            // Cheaper than a topological sort and immune to a cycle in the graph.
            var pending = new List<string>(closure);
            while (pending.Count > 0)
            {
                var stillPending = new List<string>();
                foreach (var path in pending)
                {
                    if (!NativeLibrary.TryLoad(path, out _))
                        stillPending.Add(path);
                }

                if (stillPending.Count == pending.Count)
                    break; // a whole pass with nothing loaded: the rest is not going to load either

                pending = stillPending;
            }
        }

        /// <summary>Absolute paths of <see cref="Roots"/> present in the directory plus everything
        /// they reach through <c>@rpath</c>, breadth-first.</summary>
        internal static List<string> BuildClosure(string directory)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            var closure = new List<string>();

            foreach (var root in Roots)
            {
                if (seen.Add(root))
                    queue.Enqueue(root);
            }

            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                var path = Path.Combine(directory, name);
                if (!File.Exists(path))
                    continue; // a root this payload does not ship, or a dependency living elsewhere

                closure.Add(path);

                foreach (var dependency in ReadRpathDependencies(path))
                {
                    if (seen.Add(dependency))
                        queue.Enqueue(dependency);
                }
            }

            return closure;
        }

        // ------------------------------------------------------------------------- Mach-O

        private const uint FatMagicBigEndian = 0xCAFEBABE;
        private const uint FatMagic64BigEndian = 0xCAFEBABF;
        private const uint MachMagic64 = 0xFEEDFACF;

        private const uint LcLoadDylib = 0x0000000C;
        private const uint LcLoadWeakDylib = 0x80000018;
        private const uint LcReexportDylib = 0x8000001F;

        /// <summary>
        /// The <c>@rpath/…</c> libraries <paramref name="path"/> links against, as bare file names.
        /// Absolute dependencies (<c>/usr/lib/…</c>, system frameworks) are skipped — dyld resolves
        /// those itself and always could.
        /// </summary>
        internal static IEnumerable<string> ReadRpathDependencies(string path)
        {
            const string prefix = "@rpath/";
            foreach (var dependency in ReadDependencies(path))
            {
                if (dependency.StartsWith(prefix, StringComparison.Ordinal))
                    yield return dependency.Substring(prefix.Length);
            }
        }

        /// <summary>
        /// Every library <paramref name="path"/> links against, exactly as its load commands name
        /// them — <c>@rpath/…</c>, <c>/usr/lib/…</c> and all. Empty for anything that is not a
        /// Mach-O we understand, or that cannot be read at all.
        /// <para>
        /// <c>LC_ID_DYLIB</c> is deliberately not among them. That command carries the file's own
        /// install name, which for a copy of a versioned library is itself an <c>@rpath</c> string
        /// — counting it would have the preloader treat a library as its own dependency.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<string> ReadDependencies(string path)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch
            {
                return Array.Empty<string>();
            }

            var results = new List<string>();
            try
            {
                ReadInto(bytes, results);
            }
            catch
            {
                // a truncated or unrecognized file tells us nothing; it is not ours to diagnose.
                return Array.Empty<string>();
            }

            return results;
        }

        private static void ReadInto(byte[] bytes, List<string> results)
        {
            var span = new ReadOnlySpan<byte>(bytes);
            if (span.Length < 8)
                return;

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(span);
            if (magic == FatMagicBigEndian || magic == FatMagic64BigEndian)
            {
                // Universal binary. Every slice lists the same dependencies, so read the first one
                // and stop — which slice we are running is irrelevant to the names.
                uint archCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4));
                if (archCount == 0)
                    return;

                // fat_arch is cputype, cpusubtype, offset, …; fat_arch_64 widens offset to 8 bytes.
                int offset = magic == FatMagicBigEndian
                    ? (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8 + 8))
                    : (int)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(8 + 8));

                if (offset > 0 && offset < span.Length)
                    ReadThinInto(span.Slice(offset), results);
                return;
            }

            ReadThinInto(span, results);
        }

        private static void ReadThinInto(ReadOnlySpan<byte> span, List<string> results)
        {
            if (span.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(span) != MachMagic64)
                return; // 32-bit Mach-O is not something any of this ships as

            uint commandCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
            int cursor = 32; // sizeof(mach_header_64)

            for (uint i = 0; i < commandCount; i++)
            {
                if (cursor + 8 > span.Length)
                    return;

                uint command = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor));
                int commandSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 4));
                if (commandSize <= 0 || cursor + commandSize > span.Length)
                    return;

                if (command == LcLoadDylib || command == LcLoadWeakDylib || command == LcReexportDylib)
                {
                    // dylib_command: cmd, cmdsize, then a dylib struct whose first member is an
                    // lc_str — a byte offset, from the start of this command, to a NUL-terminated
                    // name that lives in the command's own tail.
                    int nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 8));
                    if (nameOffset > 0 && nameOffset < commandSize)
                    {
                        var tail = span.Slice(cursor + nameOffset, commandSize - nameOffset);
                        int end = tail.IndexOf((byte)0);
                        results.Add(System.Text.Encoding.UTF8.GetString(end < 0 ? tail : tail.Slice(0, end)));
                    }
                }

                cursor += commandSize;
            }
        }
    }
}
