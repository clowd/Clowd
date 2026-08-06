using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class ZipStreamComposerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ClowdZipTests", Guid.NewGuid().ToString("N"));

        public ZipStreamComposerTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch { }
        }

        private string WriteFile(string relativePath, byte[] content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, content);
            return path;
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            new Random(42).NextBytes(bytes);
            return bytes;
        }

        [Fact]
        public async Task Compose_ThroughNonSeekableStream_RoundTrips()
        {
            // files go to the archive root by name; directories recurse under their own name —
            // the same layout the temp-file spool path produces.
            var standalone = RandomBytes(300_000);
            var nested = RandomBytes(1_000);
            var text = Encoding.UTF8.GetBytes("hello from the zip composer\n");

            var filePath = WriteFile("standalone.bin", standalone);
            WriteFile(Path.Combine("mydir", "a.txt"), text);
            WriteFile(Path.Combine("mydir", "sub", "b.bin"), nested);

            var composer = ZipStreamComposer.Create(new[] { filePath, Path.Combine(_root, "mydir") });

            Assert.True(composer.HasEntries);
            Assert.Equal(standalone.Length + nested.Length + text.Length, composer.TotalSourceBytes);

            var output = new MemoryStream();
            long lastConsumed = 0, lastTotal = 0;
            await composer.WriteAsync(new ThrowOnSeekStream(output),
                (consumed, total) => { lastConsumed = consumed; lastTotal = total; }, CancellationToken.None);

            Assert.Equal(composer.TotalSourceBytes, lastConsumed);
            Assert.Equal(composer.TotalSourceBytes, lastTotal);

            using var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "mydir/a.txt", "mydir/sub/b.bin", "standalone.bin" }, names);

            Assert.Equal(standalone, ReadEntry(zip, "standalone.bin"));
            Assert.Equal(text, ReadEntry(zip, "mydir/a.txt"));
            Assert.Equal(nested, ReadEntry(zip, "mydir/sub/b.bin"));
        }

        [Fact]
        public async Task Compose_StoresAlreadyCompressedTypes_AndDeflatesTheRest()
        {
            // highly repetitive payload: deflate shrinks it dramatically, store keeps it as-is
            var repetitive = Encoding.UTF8.GetBytes(new string('a', 100_000));
            var zipNamed = WriteFile("data.zip", repetitive);
            var txtNamed = WriteFile("data.txt", repetitive);

            var composer = ZipStreamComposer.Create(new[] { zipNamed, txtNamed });

            var output = new MemoryStream();
            await composer.WriteAsync(output, null, CancellationToken.None);

            using var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
            var stored = zip.GetEntry("data.zip");
            var deflated = zip.GetEntry("data.txt");

            Assert.Equal(stored.Length, stored.CompressedLength);
            Assert.True(deflated.CompressedLength < deflated.Length / 10);
        }

        [Fact]
        public async Task Compose_NoFilesBehindPaths_HasNoEntries()
        {
            var composer = ZipStreamComposer.Create(new[] { Path.Combine(_root, "does-not-exist") });

            Assert.False(composer.HasEntries);
            Assert.Equal(0, composer.TotalSourceBytes);

            // an entry-less archive still round-trips (the caller is expected to skip it anyway)
            var output = new MemoryStream();
            await composer.WriteAsync(new ThrowOnSeekStream(output), null, CancellationToken.None);
            using var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
            Assert.Empty(zip.Entries);
        }

        [Fact]
        public async Task Compose_CancellationMidZip_Propagates()
        {
            var filePath = WriteFile("big.bin", RandomBytes(2_000_000));
            var composer = ZipStreamComposer.Create(new[] { filePath });

            using var cts = new CancellationTokenSource();
            var output = new MemoryStream();

            // cancel from inside the progress callback, i.e. between copy chunks
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                composer.WriteAsync(output, (consumed, total) => cts.Cancel(), cts.Token));
        }

        private static byte[] ReadEntry(ZipArchive zip, string name)
        {
            using var entry = zip.GetEntry(name).Open();
            using var ms = new MemoryStream();
            entry.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>A write-only stream that throws on any seek-related member, proving the
        /// composer never relies on the destination being seekable.</summary>
        private sealed class ThrowOnSeekStream : Stream
        {
            private readonly MemoryStream _inner;

            public ThrowOnSeekStream(MemoryStream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException("Length");

            public override long Position
            {
                get => throw new NotSupportedException("Position get");
                set => throw new NotSupportedException("Position set");
            }

            public override void Flush()
            { }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("Seek");
            public override void SetLength(long value) => throw new NotSupportedException("SetLength");
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        }
    }
}
