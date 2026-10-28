using System;
using System.IO;
using System.Text.RegularExpressions;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The Win11 context menu's sparse package names the install root's launcher stub three times
    /// over: the registration guard in <see cref="SparsePackageManager"/>, the manifest's
    /// Application Executable, and (implicitly) release.yml, because vpk names that stub after
    /// <c>--packTitle</c>, not after <c>--mainExe</c>. A mismatch fails silently on every user's
    /// machine (the guard defers the registration forever and logs only a debug line), so the three
    /// are pinned to each other here, read straight off the repo tree.
    /// </summary>
    public class ShellExtensionIdentityTests
    {
        private static readonly Lazy<string> _repo = new(() =>
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "crates", "prisc_shell_ext", "msix", "AppxManifest.template.xml")))
                    return dir.FullName;
            }

            throw new DirectoryNotFoundException("Could not find the repo root by walking up from " + AppContext.BaseDirectory);
        });

        private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(_repo.Value, Path.Combine(parts)));

        [Fact]
        public void ManifestExecutable_IsTheRootStubTheGuardWaitsFor()
        {
            var manifest = Read("crates", "prisc_shell_ext", "msix", "AppxManifest.template.xml");
            var executable = Regex.Match(manifest, "<Application\\s[^>]*Executable=\"([^\"]+)\"");

            Assert.True(executable.Success, "no Application Executable in the manifest");
            Assert.Equal(SparsePackageManager.RootStubFileName, executable.Groups[1].Value);
        }

        [Fact]
        public void RootStub_IsNamedAfterTheWindowsPackTitle()
        {
            var release = Read(".github", "workflows", "release.yml");

            // the Windows pack is the one passing an .exe main exe
            var pack = Regex.Match(release, "vpk pack [^\\n]*--packTitle (\\S+)[\\s\\S]*?--mainExe \\S+\\.exe");

            Assert.True(pack.Success, "no Windows vpk pack with --packTitle in release.yml");
            Assert.Equal(SparsePackageManager.RootStubFileName, pack.Groups[1].Value + ".exe");
        }
    }
}
