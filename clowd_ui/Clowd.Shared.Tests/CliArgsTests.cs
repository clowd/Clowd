using System;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class CliArgsTests
    {
        [Fact]
        public void UploadCommand_IsStripped()
        {
            var paths = CliArgs.ExtractUploadPaths(new[] { "upload", @"C:\a.txt", @"C:\b.txt" });
            Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, paths);
        }

        [Fact]
        public void UploadCommand_IsCaseInsensitive()
        {
            var paths = CliArgs.ExtractUploadPaths(new[] { "Upload", @"C:\a.txt" });
            Assert.Equal(new[] { @"C:\a.txt" }, paths);
        }

        [Fact]
        public void UploadCommand_Alone_YieldsNothing()
        {
            Assert.Empty(CliArgs.ExtractUploadPaths(new[] { "upload" }));
        }

        [Fact]
        public void LegacyBarePaths_PassThrough()
        {
            // files dragged onto the exe and pre-command shortcuts send raw paths
            var args = new[] { @"C:\a.txt", @"C:\b.txt" };
            Assert.Equal(args, CliArgs.ExtractUploadPaths(args));
        }

        [Fact]
        public void PathNamedUpload_IsNotMistakenForTheCommand()
        {
            // real paths are always fully qualified, so only a bare token is the command
            var args = new[] { @"C:\files\upload" };
            Assert.Equal(args, CliArgs.ExtractUploadPaths(args));
        }

        [Fact]
        public void NullOrEmpty_YieldsNothing()
        {
            Assert.Empty(CliArgs.ExtractUploadPaths(null));
            Assert.Empty(CliArgs.ExtractUploadPaths(Array.Empty<string>()));
        }
    }
}
