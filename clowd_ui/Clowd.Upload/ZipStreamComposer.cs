using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload
{
    internal delegate void ZipProgressHandler(long sourceBytesConsumed, long totalSourceBytes);

    /// <summary>
    /// Streams a zip archive of files and directories into a destination stream without ever
    /// seeking it (ZipArchive in Create mode emits data descriptors when the output can't seek),
    /// so the archive can be piped straight into an upload instead of spooled to a temp file.
    /// The layout matches the temp-file zip path: files at the archive root by filename,
    /// directories recursively under their own name.
    /// </summary>
    internal sealed class ZipStreamComposer
    {
        private static readonly IMimeProvider _mime = new MimeProvider();

        private readonly List<(string SourcePath, string EntryName)> _entries;

        /// <summary>Sum of the input file sizes, known up front so upload progress can be
        /// displayed as a byte ratio even though the compressed size isn't known yet.</summary>
        public long TotalSourceBytes { get; }

        public bool HasEntries => _entries.Count > 0;

        private ZipStreamComposer(List<(string SourcePath, string EntryName)> entries, long totalSourceBytes)
        {
            _entries = entries;
            TotalSourceBytes = totalSourceBytes;
        }

        /// <summary>A recursive walk that steps over what it is not allowed to read instead of
        /// throwing, so one ACL-protected subfolder cannot sink the whole archive. AttributesToSkip
        /// is cleared because the default hides hidden/system files, which the temp-file zip path
        /// (SearchOption.AllDirectories) includes.</summary>
        private static readonly EnumerationOptions _walkOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
        };

        public static ZipStreamComposer Create(string[] paths)
        {
            var entries = new List<(string, string)>();
            long total = 0;

            void add(string file, string entryName)
            {
                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // unreadable or already gone — leave it out rather than fail the archive
                    return;
                }

                entries.Add((file, entryName));
                total += length;
            }

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var root = Path.GetFullPath(path);
                    var rootName = Path.GetFileName(root);
                    foreach (var file in Directory.EnumerateFiles(root, "*", _walkOptions))
                        add(file, Path.Combine(rootName, Path.GetRelativePath(root, file)).Replace('\\', '/'));
                }
                else if (File.Exists(path))
                {
                    add(path, Path.GetFileName(path));
                }
            }

            return new ZipStreamComposer(entries, total);
        }

        public async Task WriteAsync(Stream destination, ZipProgressHandler progress, CancellationToken cancelToken)
        {
            long consumed = 0;
            var buffer = new byte[80 * 1024];

            // always write through the unseekable wrapper so the produced archive is identical
            // whether the destination is a pipe or a seekable test stream.
            using var zip = new ZipArchive(new UnseekableWriteStream(destination), ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (sourcePath, entryName) in _entries)
            {
                cancelToken.ThrowIfCancellationRequested();

                var entry = zip.CreateEntry(entryName, GetCompressionLevel(entryName));

                // mirror CreateEntryFromFile: stamp the source timestamp when it fits zip's range
                var lastWrite = File.GetLastWriteTime(sourcePath);
                if (lastWrite.Year >= 1980 && lastWrite.Year <= 2107)
                    entry.LastWriteTime = lastWrite;

                using var source = File.OpenRead(sourcePath);
                using var dest = entry.Open();
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancelToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancelToken);
                    consumed += read;
                    progress?.Invoke(consumed, TotalSourceBytes);
                }
            }
        }

        private static CompressionLevel GetCompressionLevel(string entryName)
        {
            var ext = Path.GetExtension(entryName);
            if (String.IsNullOrEmpty(ext))
                return CompressionLevel.Optimal;

            // already-compressed content (media, archives) is stored rather than deflated again
            var mime = _mime.GetMimeFromExtension(ext);
            if (mime.Compressible == false || _mime.GetCategoryFromExtension(ext) == ContentCategory.Compressed)
                return CompressionLevel.NoCompression;

            return CompressionLevel.Optimal;
        }

        /// <summary>Hides seekability so ZipArchive takes its forward-only path (local headers
        /// deferred to data descriptors) regardless of the underlying stream.</summary>
        private sealed class UnseekableWriteStream : Stream
        {
            private readonly Stream _inner;

            public UnseekableWriteStream(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
