using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class DangerousFileTypesTests
    {
        [Theory]
        [InlineData("exe")]
        [InlineData(".exe")]
        [InlineData(".EXE")]
        [InlineData("Exe")]
        [InlineData(".msi")]
        [InlineData("msixbundle")]
        [InlineData(".bat")]
        [InlineData("cmd")]
        [InlineData(".ps1")]
        [InlineData("js")]
        [InlineData(".lnk")]
        [InlineData("url")]
        [InlineData(".iso")]
        [InlineData("vhdx")]
        [InlineData(" .exe ")]
        public void IsDangerous_KnownRiskyExtensions_True(string extension)
        {
            Assert.True(DangerousFileTypes.IsDangerous(extension));
        }

        [Theory]
        [InlineData("txt")]
        [InlineData(".png")]
        [InlineData("pdf")]
        [InlineData(".zip")]
        [InlineData("mp4")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData(".")]
        public void IsDangerous_SafeOrEmptyExtensions_False(string extension)
        {
            Assert.False(DangerousFileTypes.IsDangerous(extension));
        }
    }
}
