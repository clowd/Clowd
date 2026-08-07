using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Upload.Accelerate;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class SigV4PresignerTests
    {
        // The canonical "GET Object" presigned-URL example from the AWS SigV4 documentation:
        // https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-query-string-auth.html
        // access key AKIAIOSFODNN7EXAMPLE / secret wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY,
        // us-east-1 / s3, host examplebucket.s3.amazonaws.com, GET /test.txt, 86400s,
        // signed at 2013-05-24T00:00:00Z, UNSIGNED-PAYLOAD, SignedHeaders=host.
        [Fact]
        public void Presign_MatchesAwsDocumentedGetObjectVector()
        {
            var url = SigV4Presigner.Presign(
                "GET",
                new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),
                new Dictionary<string, string>(),
                "AKIAIOSFODNN7EXAMPLE",
                "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                "us-east-1",
                "s3",
                TimeSpan.FromSeconds(86400),
                new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));

            Assert.Contains("X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", url);
            Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", url);
            Assert.Contains("X-Amz-SignedHeaders=host", url);
            Assert.Contains("X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request", url);
            Assert.Contains("X-Amz-Expires=86400", url);
            Assert.StartsWith("https://examplebucket.s3.amazonaws.com/test.txt?", url);
        }

        [Fact]
        public void Presign_SortsAndEncodesExtraQueryParams()
        {
            var url = SigV4Presigner.Presign(
                "PUT",
                new Uri("https://s3.eu-west-2.amazonaws.com/my-bucket/abc/file.bin"),
                new Dictionary<string, string> { ["partNumber"] = "1", ["uploadId"] = "abc+def/ghi=" },
                "AKIAEXAMPLE",
                "secret",
                "eu-west-2",
                "s3",
                TimeSpan.FromHours(48),
                DateTimeOffset.UtcNow);

            // uploadId's reserved chars must be RFC 3986 percent-encoded in the query.
            Assert.Contains("uploadId=abc%2Bdef%2Fghi%3D", url);
            Assert.Contains("partNumber=1", url);
            // canonical query is sorted: X-Amz-Algorithm precedes partNumber precedes uploadId.
            Assert.True(url.IndexOf("X-Amz-Algorithm", StringComparison.Ordinal) <
                        url.IndexOf("partNumber=", StringComparison.Ordinal));
        }

        [Fact]
        public void Presign_NonDefaultPort_IncludedInHost()
        {
            var url = SigV4Presigner.Presign(
                "PUT",
                new Uri("http://localhost:9000/bucket/key"),
                new Dictionary<string, string> { ["uploadId"] = "x" },
                "key", "secret", "us-east-1", "s3", TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

            Assert.StartsWith("http://localhost:9000/bucket/key?", url);
        }
    }

    public class AcceleratedUploadPlanTests
    {
        [Theory]
        [InlineData(0, 16 * 1024 * 1024, 1)]                     // zero-byte -> one (empty) chunk
        [InlineData(1, 16 * 1024 * 1024, 1)]
        [InlineData(16 * 1024 * 1024, 16 * 1024 * 1024, 1)]      // exact multiple
        [InlineData(16 * 1024 * 1024 + 1, 16 * 1024 * 1024, 2)]  // one byte over -> second chunk
        [InlineData(44L * 16 * 1024 * 1024, 16 * 1024 * 1024, 44)]
        public void ComputeChunkCount_MatchesCeilingDivision(long length, long chunkSize, int expected)
        {
            Assert.Equal(expected, AcceleratedUploadClient.ComputeChunkCount(length, chunkSize));
        }

        [Theory]
        [InlineData(0, 16 * 1024 * 1024)]                          // 0 -> default
        [InlineData(1024, 5 * 1024 * 1024)]                        // below floor -> 5 MiB
        [InlineData(16 * 1024 * 1024, 16 * 1024 * 1024)]           // in range unchanged
        [InlineData(64L * 1024 * 1024, 32 * 1024 * 1024)]          // above ceiling -> 32 MiB
        public void ClampChunkSize_KeepsWithinServerRange(long requested, long expected)
        {
            Assert.Equal(expected, AcceleratedUploadClient.ClampChunkSize(requested));
        }

        [Fact]
        public void DefaultChunkSize_IsInsideServerClampRange()
        {
            // 16 MiB must survive the server's [5,32] MiB clamp unchanged so the client chunk plan
            // (and any S3 part URLs minted for it) stays consistent with what the server relays.
            Assert.Equal(AcceleratedUploadClient.DefaultChunkSize,
                         AcceleratedUploadClient.ClampChunkSize(AcceleratedUploadClient.DefaultChunkSize));
        }
    }

    public class UnknownLengthChunkerTests
    {
        // a stream that hides its length and dribbles data out a few bytes per read, like a
        // pipe fed by a live zip compressor.
        private sealed class DribbleStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _maxRead;
            private int _pos;

            public DribbleStream(byte[] data, int maxRead)
            {
                _data = data;
                _maxRead = maxRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = Math.Min(Math.Min(count, _maxRead), _data.Length - _pos);
                Array.Copy(_data, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
        }

        private static byte[] Sequence(int length)
            => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

        private static async Task<List<(byte[] Bytes, bool IsFinal)>> ReadAll(byte[] data, long chunkSize, int maxRead = 3)
        {
            var chunker = new UnknownLengthChunker(new DribbleStream(data, maxRead), chunkSize);
            var chunks = new List<(byte[], bool)>();
            while (true)
            {
                var (length, isFinal) = await chunker.ReadNextAsync(CancellationToken.None).ConfigureAwait(false);
                chunks.Add((chunker.Buffer.Take(length).ToArray(), isFinal));
                if (isFinal)
                    return chunks;
            }
        }

        [Theory]
        [InlineData(1)]        // single short final chunk
        [InlineData(7)]        // just under the boundary
        [InlineData(8)]        // exactly one chunk -> full-size final is legal
        [InlineData(9)]        // one byte over -> (8, false) + (1, true)
        [InlineData(16)]       // exact multiple -> full-size final chunk, no empty tail
        [InlineData(17)]
        [InlineData(24)]
        [InlineData(100)]
        public async Task ChunksAreExactSize_ExceptFinal_AndOnlyLastIsFinal(int total)
        {
            const long chunkSize = 8;
            var chunks = await ReadAll(Sequence(total), chunkSize).ConfigureAwait(true);

            var expectedCount = (total + (int)chunkSize - 1) / (int)chunkSize;
            Assert.Equal(expectedCount, chunks.Count);

            // every chunk except the last is exactly chunkSize and not final
            foreach (var (bytes, isFinal) in chunks.Take(chunks.Count - 1))
            {
                Assert.Equal(chunkSize, bytes.Length);
                Assert.False(isFinal);
            }

            // the final chunk is marked, and is 1..=chunkSize bytes
            var last = chunks[^1];
            Assert.True(last.IsFinal);
            Assert.InRange(last.Bytes.Length, 1, (int)chunkSize);

            // reassembling the chunks yields the original bytes (the lookahead byte carried
            // across every chunk boundary must not be lost or duplicated)
            Assert.Equal(Sequence(total), chunks.SelectMany(c => c.Bytes).ToArray());
        }

        [Fact]
        public async Task EmptyStream_ReportsZeroLengthFinal()
        {
            var chunker = new UnknownLengthChunker(new DribbleStream(Array.Empty<byte>(), 3), 8);
            var (length, isFinal) = await chunker.ReadNextAsync(CancellationToken.None).ConfigureAwait(true);

            // no valid chunk exists (the protocol rejects zero-byte chunks); the caller turns
            // this into an error rather than sending it.
            Assert.Equal(0, length);
            Assert.True(isFinal);
        }

        [Fact]
        public async Task ReadingPastFinal_Throws()
        {
            var chunker = new UnknownLengthChunker(new DribbleStream(Sequence(5), 3), 8);
            var (_, isFinal) = await chunker.ReadNextAsync(CancellationToken.None).ConfigureAwait(true);
            Assert.True(isFinal);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => chunker.ReadNextAsync(CancellationToken.None)).ConfigureAwait(true);
        }

        [Fact]
        public async Task BufferContents_StableForRetry_UntilNextRead()
        {
            // a failed PUT retries from the same buffer; the chunk bytes must still be there.
            var data = Sequence(20);
            var chunker = new UnknownLengthChunker(new DribbleStream(data, 3), 8);

            var (length, _) = await chunker.ReadNextAsync(CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(8, length);
            Assert.Equal(data.Take(8), chunker.Buffer.Take(8));
            // read again ("retry") without advancing — same bytes
            Assert.Equal(data.Take(8), chunker.Buffer.Take(8));

            var (length2, _) = await chunker.ReadNextAsync(CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(8, length2);
            Assert.Equal(data.Skip(8).Take(8), chunker.Buffer.Take(8));
        }
    }

    public class AccelerateJsonSerializationTests
    {
        [Fact]
        public void CreateRequest_KnownLength_SendsCamelCaseContentLength()
        {
            var json = JsonSerializer.Serialize(new CreateUploadRequest
            {
                FileName = "file.zip",
                ContentType = "application/zip",
                ContentLength = 12345,
                ChunkSize = 16 * 1024 * 1024,
                Destination = new DestinationDescriptor { Type = "discard" },
            }, AccelerateJsonContext.Default.CreateUploadRequest);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(12345, doc.RootElement.GetProperty("contentLength").GetInt64());
            Assert.Equal(16 * 1024 * 1024, doc.RootElement.GetProperty("chunkSize").GetInt64());
        }

        [Fact]
        public void CreateRequest_UnknownLength_OmitsContentLength()
        {
            var json = JsonSerializer.Serialize(new CreateUploadRequest
            {
                FileName = "file.zip",
                ContentType = "application/zip",
                ContentLength = null,
                ChunkSize = 16 * 1024 * 1024,
                Destination = new DestinationDescriptor { Type = "discard" },
            }, AccelerateJsonContext.Default.CreateUploadRequest);

            // absent and null are equivalent on the wire (both mean "unknown length"); the
            // context's WhenWritingNull emits the absent form.
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("contentLength", out _));
        }

        [Fact]
        public void S3Descriptor_UnknownLength_SendsEmptyPartUrlsArray()
        {
            // the spec requires partUrls to be present-and-empty (not omitted) for an
            // unknown-length s3-multipart create.
            var json = JsonSerializer.Serialize(new DestinationDescriptor
            {
                Type = "s3-multipart",
                PartUrls = Array.Empty<string>(),
                CompleteUrl = "https://example.com/complete",
                AbortUrl = "https://example.com/abort",
                FinalUrl = "https://example.com/final",
            }, AccelerateJsonContext.Default.DestinationDescriptor);

            using var doc = JsonDocument.Parse(json);
            var partUrls = doc.RootElement.GetProperty("partUrls");
            Assert.Equal(JsonValueKind.Array, partUrls.ValueKind);
            Assert.Equal(0, partUrls.GetArrayLength());
        }
    }

    public class AcceleratedDeleteTokenTests
    {
        [Fact]
        public void RoundTrips_IdAndToken()
        {
            var encoded = AcceleratedDeleteToken.Encode("aj20lajk", "s3cr3t-token");
            Assert.True(AcceleratedDeleteToken.TryParse(encoded, out var id, out var token));
            Assert.Equal("aj20lajk", id);
            Assert.Equal("s3cr3t-token", token);
        }

        [Fact]
        public void PreservesColonsInsideToken()
        {
            var encoded = AcceleratedDeleteToken.Encode("id123", "a:b:c");
            Assert.True(AcceleratedDeleteToken.TryParse(encoded, out var id, out var token));
            Assert.Equal("id123", id);
            Assert.Equal("a:b:c", token);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("deletehash-from-imgur")]  // legacy provider delete key
        [InlineData("clwd:v1:")]               // no id/token
        [InlineData("clwd:v1:onlyid")]         // missing token
        public void TryParse_ReturnsFalse_ForNonAcceleratedKeys(string value)
        {
            Assert.False(AcceleratedDeleteToken.TryParse(value, out _, out _));
        }
    }
}
