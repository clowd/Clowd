using System;
using System.Collections.Generic;
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
