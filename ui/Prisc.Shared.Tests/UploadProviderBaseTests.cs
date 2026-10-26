using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class UploadProviderBaseTests
    {
        [Fact]
        public async Task TestAsync_TextProvider_UploadsTextAndDeletesResult()
        {
            var provider = new FakeUploadProvider(SupportedUploadType.Text | SupportedUploadType.Image)
            {
                ShouldDelete = true,
                Result = new UploadResult
                {
                    PublicUrl = "https://example.com/clowd-test.txt",
                    UploadKey = "upload-key",
                    DeleteKey = "delete-key",
                    FileName = "stored-name.txt",
                },
            };

            await provider.TestAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal("clowd-test.txt", provider.UploadName);
            Assert.Equal("Clowd upload test\n", Encoding.UTF8.GetString(provider.UploadedBytes));
            Assert.True(provider.ProgressWasProvided);
            Assert.True(provider.DeleteCalled);
            Assert.Equal("upload-key", provider.DeleteInfo.UploadKey);
            Assert.Equal("delete-key", provider.DeleteInfo.DeleteKey);
            Assert.Equal("stored-name.txt", provider.DeleteInfo.FileName);
            Assert.Equal("https://example.com/clowd-test.txt", provider.DeleteInfo.PublicUrl);
        }

        [Fact]
        public async Task TestAsync_ImageProvider_UploadsValidPngPayload()
        {
            var provider = new FakeUploadProvider(SupportedUploadType.Image);

            await provider.TestAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal("clowd-test.png", provider.UploadName);
            Assert.True(provider.UploadedBytes.Length > 8);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, provider.UploadedBytes[..8]);
        }

        [Fact]
        public async Task TestAsync_MissingPublicUrl_Throws()
        {
            var provider = new FakeUploadProvider(SupportedUploadType.Text)
            {
                ShouldDelete = true,
                Result = new UploadResult { PublicUrl = " " },
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TestAsync(CancellationToken.None)).ConfigureAwait(true);

            Assert.Contains("public URL", exception.Message);
            Assert.False(provider.DeleteCalled);
        }

        [Fact]
        public async Task TestAsync_CleanupFailure_IsIgnored()
        {
            var provider = new FakeUploadProvider(SupportedUploadType.Text)
            {
                ShouldDelete = true,
                ThrowOnDelete = true,
            };

            await provider.TestAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.True(provider.DeleteCalled);
        }

        private sealed class FakeUploadProvider : UploadProviderBase
        {
            public FakeUploadProvider()
                : this(SupportedUploadType.Text)
            { }

            public FakeUploadProvider(SupportedUploadType supportedUpload)
            {
                SupportedUpload = supportedUpload;
            }

            public override SupportedUploadType SupportedUpload { get; }
            public override string Name => "Fake";
            public override string Description => "Fake upload provider";
            public override Stream Icon => Stream.Null;

            public UploadResult Result { get; set; } = new UploadResult { PublicUrl = "https://example.com/upload" };
            public byte[] UploadedBytes { get; private set; }
            public string UploadName { get; private set; }
            public bool ProgressWasProvided { get; private set; }
            public bool ShouldDelete { get; set; }
            public bool ThrowOnDelete { get; set; }
            public bool DeleteCalled { get; private set; }
            public UploadDeleteInfo DeleteInfo { get; private set; }

            public override async Task<UploadResult> UploadAsync(
                Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
            {
                using var output = new MemoryStream();
                await fileStream.CopyToAsync(output, cancelToken).ConfigureAwait(false);
                UploadedBytes = output.ToArray();
                UploadName = uploadName;
                ProgressWasProvided = progress != null;
                progress?.Invoke(UploadedBytes.Length);
                return Result;
            }

            public override bool CanDelete(UploadDeleteInfo info)
            {
                DeleteInfo = info;
                return ShouldDelete;
            }

            public override Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
            {
                DeleteCalled = true;
                if (ThrowOnDelete)
                    throw new InvalidOperationException("Cleanup failed");

                return Task.CompletedTask;
            }
        }
    }
}
