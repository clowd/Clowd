using System;
using System.IO;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Where the app looks for the recorder and the helpers that ship beside it. CI only ever
    /// exercises the environment-variable branch (test-video sets PRISC_RECORDER_PATH), so the
    /// release layout and the dev walk-up are pinned here. Every tree lives under the temp dir,
    /// never under the repo, or the walk-up would find the real target/ dir. Only the pure
    /// overloads are called: nothing reads or sets process environment.
    /// </summary>
    public sealed class ObsBinaryLocatorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PriscLocatorTests", Guid.NewGuid().ToString("N"));

        public ObsBinaryLocatorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch { }
        }

        private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
            return path;
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact]
        public void BinaryNames_MatchTheShippedPayload()
        {
            // ci.yml / build-recorder.yml stage exactly these names into publish/recorder
            Assert.Equal(Exe("prisc_recorder"), ObsBinaryLocator.BinaryFileName);
            Assert.Equal(Exe("prisc_share_region"), ShareRegionDriver.BinaryFileName);
        }

        [Fact]
        public void ReleaseLayout_IsTheRecorderFolderNextToTheApp()
        {
            var app = Dir("app");
            var recorder = Touch("app", "recorder", ObsBinaryLocator.BinaryFileName);

            Assert.Equal(recorder, ObsBinaryLocator.Resolve(null, app));
        }

        [Fact]
        public void OtherLayouts_AreNotFound()
        {
            // the pre-rename shapes: a differently named payload folder beside the app, and a
            // separate sibling checkout of the recorder next to the workspace
            Touch("Cargo.toml");
            var app = Dir("app");
            Touch("app", "old-payload", ObsBinaryLocator.BinaryFileName);
            Touch("app", ObsBinaryLocator.BinaryFileName);
            Touch("sibling-checkout", "target", "debug", ObsBinaryLocator.BinaryFileName);

            Assert.Null(ObsBinaryLocator.Resolve(null, app));
        }

        [Fact]
        public void DevLayout_FindsTheWorkspaceTarget_PreferringDebug()
        {
            // <repo>/Cargo.toml with the build output; the nested crate manifest (no target/) is
            // walked past
            Touch("repo", "Cargo.toml");
            Touch("repo", "crates", "foo", "Cargo.toml");
            var debug = Touch("repo", "target", "debug", ObsBinaryLocator.BinaryFileName);
            var release = Touch("repo", "target", "release", ObsBinaryLocator.BinaryFileName);
            var app = Dir("repo", "crates", "foo", "bin", "Debug", "net10.0");

            Assert.Equal(debug, ObsBinaryLocator.Resolve(null, app));

            File.Delete(debug);
            Assert.Equal(release, ObsBinaryLocator.Resolve(null, app));

            File.Delete(release);
            Assert.Null(ObsBinaryLocator.Resolve(null, app));
        }

        [Fact]
        public void ReleaseLayout_WinsOverADevWorkspace()
        {
            Touch("repo", "Cargo.toml");
            Touch("repo", "target", "debug", ObsBinaryLocator.BinaryFileName);
            var app = Dir("repo", "ui", "bin");
            var shipped = Touch("repo", "ui", "bin", "recorder", ObsBinaryLocator.BinaryFileName);

            Assert.Equal(shipped, ObsBinaryLocator.Resolve(null, app));
        }

        [Fact]
        public void EnvVar_Wins_AndAMissingOrBlankOneFallsThrough()
        {
            var app = Dir("app");
            var shipped = Touch("app", "recorder", ObsBinaryLocator.BinaryFileName);
            var custom = Touch("custom", ObsBinaryLocator.BinaryFileName);

            Assert.Equal(custom, ObsBinaryLocator.Resolve(custom, app));
            Assert.Equal(shipped, ObsBinaryLocator.Resolve(Path.Combine(_root, "missing", ObsBinaryLocator.BinaryFileName), app));
            Assert.Equal(shipped, ObsBinaryLocator.Resolve("  ", app));
        }

        [Fact]
        public void Helpers_ResolveBesideTheRecorder()
        {
            var recorder = Touch("recorder", ObsBinaryLocator.BinaryFileName);
            var shareRegion = Touch("recorder", ShareRegionDriver.BinaryFileName);
            var vid2gif = Touch("recorder", Exe("prisc_vid2gif"));

            Assert.Equal(shareRegion, HelperBinary.ResolveBesideRecorder(null, () => recorder, ShareRegionDriver.BinaryFileName));
            Assert.Equal(vid2gif, HelperBinary.ResolveBesideRecorder("", () => recorder, Exe("prisc_vid2gif")));

            // missing sibling, or no recorder at all
            File.Delete(vid2gif);
            Assert.Null(HelperBinary.ResolveBesideRecorder(null, () => recorder, Exe("prisc_vid2gif")));
            Assert.Null(HelperBinary.ResolveBesideRecorder(null, () => null, ShareRegionDriver.BinaryFileName));
        }

        [Fact]
        public void Helpers_TheirOwnOverrideWins_WithoutAskingForTheRecorder()
        {
            var custom = Touch("custom", ShareRegionDriver.BinaryFileName);

            Assert.Equal(custom, HelperBinary.ResolveBesideRecorder(custom, () => throw new InvalidOperationException("not asked"),
                ShareRegionDriver.BinaryFileName));

            // an override naming nothing falls back to the recorder's directory
            var recorder = Touch("recorder", ObsBinaryLocator.BinaryFileName);
            var beside = Touch("recorder", ShareRegionDriver.BinaryFileName);
            Assert.Equal(beside, HelperBinary.ResolveBesideRecorder(Path.Combine(_root, "nope.exe"), () => recorder,
                ShareRegionDriver.BinaryFileName));
        }
    }
}
