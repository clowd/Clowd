using System;
using System.IO;
using Clowd.Config;
using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class UploadRoutingTests : IDisposable
    {
        private static readonly IMimeProvider _mime = new MimeProvider();

        private readonly string _path = Path.Combine(Path.GetTempPath(), "ClowdSettingsTests", Guid.NewGuid() + ".json");

        public void Dispose()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch { }
        }

        private static ZipDecision Decide(string[] paths, bool wrapDangerous, bool exists = true, long length = 1000)
            => UploadRouting.ShouldZip(paths, wrapDangerous, _mime, _ => exists, _ => length);

        [Fact]
        public void MultiplePaths_AlwaysZip_WithRandomName()
        {
            var decision = Decide(new[] { @"C:\a.txt", @"C:\b.txt" }, wrapDangerous: true);

            Assert.True(decision.Zip);
            Assert.Null(decision.ArchiveName);
        }

        [Fact]
        public void SingleMissingPath_Zips()
        {
            // a directory (or a path that no longer exists) is not an existing *file*
            var decision = Decide(new[] { @"C:\some-directory" }, wrapDangerous: true, exists: false);

            Assert.True(decision.Zip);
            Assert.Null(decision.ArchiveName);
        }

        [Fact]
        public void SingleImage_UploadsDirect()
        {
            var decision = Decide(new[] { @"C:\photo.png" }, wrapDangerous: true, length: 50 * 1024 * 1024);

            Assert.False(decision.Zip);
        }

        [Fact]
        public void SingleLargeUnknownCompressible_Zips()
        {
            // font/ttf: compressible, category unknown — over 5 MB the old heuristic zips it
            var decision = Decide(new[] { @"C:\font.ttf" }, wrapDangerous: true, length: 10 * 1024 * 1024);

            Assert.True(decision.Zip);
            Assert.Null(decision.ArchiveName);
        }

        [Fact]
        public void SingleSmallUnknownCompressible_UploadsDirect()
        {
            var decision = Decide(new[] { @"C:\font.ttf" }, wrapDangerous: true, length: 1024);

            Assert.False(decision.Zip);
        }

        [Fact]
        public void DangerousFile_WithWrappingOn_ZipsUnderOriginalName()
        {
            var decision = Decide(new[] { Path.Combine("downloads", "tool.exe") }, wrapDangerous: true);

            Assert.True(decision.Zip);
            Assert.Equal("tool.exe.zip", decision.ArchiveName);
        }

        [Fact]
        public void DangerousFile_WithWrappingOff_FollowsNormalHeuristic()
        {
            // .exe maps to application/octet-stream (compressible: false), so even a large one
            // goes direct when wrapping is off — exactly the pre-existing behavior.
            var small = Decide(new[] { @"C:\downloads\tool.exe" }, wrapDangerous: false);
            var large = Decide(new[] { @"C:\downloads\tool.exe" }, wrapDangerous: false, length: 50 * 1024 * 1024);

            Assert.False(small.Zip);
            Assert.False(large.Zip);
        }

        [Fact]
        public void DangerousFile_CaseInsensitive_StillWrapped()
        {
            var decision = Decide(new[] { Path.Combine("downloads", "SETUP.MSI") }, wrapDangerous: true);

            Assert.True(decision.Zip);
            Assert.Equal("SETUP.MSI.zip", decision.ArchiveName);
        }

        [Fact]
        public void SupportsUnseekableUpload_TrueOnlyForStreamingProviders()
        {
            Assert.True(new S3UploadProvider().SupportsUnseekableUpload);
            Assert.True(new AzureUploadProvider().SupportsUnseekableUpload);
            Assert.True(new CloudflareR2UploadProvider().SupportsUnseekableUpload);
            Assert.True(new BackBlazeUploadProvider().SupportsUnseekableUpload);

            // everything else keeps the temp-file spool path
            Assert.False(new ImgurUploadProvider().SupportsUnseekableUpload);
            Assert.False(new CatboxUploadProvider().SupportsUnseekableUpload);
        }

        [Fact]
        public void WrapDangerousUploadsInZip_DefaultsOn_AndRoundTrips()
        {
            var loaded = SettingsService.Load(_path);
            Assert.True(loaded.Uploads.WrapDangerousUploadsInZip);

            var original = new SettingsRoot();
            original.Uploads.WrapDangerousUploadsInZip = false;

            SettingsService.Save(original, _path);
            loaded = SettingsService.Load(_path);

            Assert.False(loaded.Uploads.WrapDangerousUploadsInZip);
        }
    }
}
