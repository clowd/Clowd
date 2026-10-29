using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Prisc.Ipc;
using Prisc.UI.Services;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="JobRunner"/> against the real Prisc.VideoRender child, found through
    /// <see cref="HelperLocator.Override"/> the way the app finds it. That seam is process-wide, so
    /// these share a collection with the other tests that set it and never run in parallel with
    /// them. They skip when FFmpeg or the built render tool is missing.
    /// </summary>
    [Collection("HelperLocator.Override")]
    public sealed class JobRunnerTests : IDisposable
    {
        private const string RenderStem = HelperNames.VideoRender;
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

        /// <summary>The render tool's test knob: it waits after its first frame until the quit
        /// arrives, so the cancel always lands mid-render however long the host takes to send it.
        /// A run that never stops the tool still fails these (the hold gives up long after the
        /// quit grace and the render ends done{ok}); a quit lost on the wire is the
        /// RenderArgsCompatTests cancel test's to catch, as there it shows up as a kill.</summary>
        internal static readonly IReadOnlyDictionary<string, string> HoldForQuit =
            new Dictionary<string, string> { [Prisc.Interop.EnvVars.RenderTestHoldForQuit] = "1" };

        private readonly List<string> _tempPaths = new();

        public void Dispose()
        {
            HelperLocator.Override = null;
            foreach (var path in _tempPaths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch { /* best effort */ }
            }
        }

        private string TempPath(string extension)
        {
            string path = Path.Combine(Path.GetTempPath(), $"prisc-jobrunner-test-{Guid.NewGuid():N}{extension}");
            _tempPaths.Add(path);
            return path;
        }

        /// <summary>Points the locator at the render tool's apphost beside its built dll, and writes a
        /// v1 args file rendering the first <paramref name="seconds"/> of a fresh fixture.</summary>
        private (string ArgsPath, string Output) Prepare(int seconds)
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            string dll = RenderArgsCompatTests.FindRenderDll();
            Assert.SkipWhen(dll == null, "Prisc.VideoRender.dll not found: build the exe project.");
            string exe = Path.Combine(Path.GetDirectoryName(dll)!, Prisc.Interop.NativeProbe.ExeName(RenderStem));
            Assert.SkipUnless(File.Exists(exe), "Prisc.VideoRender has no apphost beside " + dll);
            HelperLocator.Override = stem => stem == RenderStem ? exe : null;

            string input = TempPath(".mp4");
            RenderArgsCompatTests.WriteFixtureMp4(input, 64, 64, 30, seconds, 48000);
            string output = TempPath(".mp4");
            string argsPath = TempPath(".json");
            File.WriteAllText(argsPath, RenderArgsCompatTests.ArgsJson(input, output,
                segments: $"[{{\"start_ms\":0,\"end_ms\":{seconds * 1000}}}]", crf: "30"));
            return (argsPath, output);
        }

        [Fact]
        public async Task A_render_runs_to_success()
        {
            var (argsPath, output) = Prepare(seconds: 1);

            using var runner = JobRunner.ForVideoRender(argsPath);
            var result = await runner.RunAsync().WaitAsync(Bound);

            Assert.Equal(JobOutcome.Success, result.Outcome);
            Assert.Equal(output, result.OutputPath);
            Assert.True(result.Bytes > 0);
            Assert.Equal(result.Bytes, new FileInfo(output).Length);
        }

        /// <summary>The round-1 race: a cancel that lands before <see cref="JobRunner.RunAsync"/> has
        /// published its client has nobody to send quit to, so the run must pick it up itself and
        /// still end canceled, with the partial output deleted by the helper.</summary>
        [Fact]
        public async Task A_cancel_before_the_client_exists_ends_the_run_canceled()
        {
            var (argsPath, output) = Prepare(seconds: 5);

            using var runner = JobRunner.ForVideoRender(argsPath, HoldForQuit);
            await runner.CancelAsync().WaitAsync(Bound);
            var result = await runner.RunAsync().WaitAsync(Bound);

            Assert.Equal(JobOutcome.Canceled, result.Outcome);
            Assert.False(File.Exists(output));
        }

        /// <summary>The same race with the cancel genuinely in flight: whether it lands while the
        /// helper is starting or once the client is published, the run ends canceled.</summary>
        [Fact]
        public async Task A_cancel_racing_the_start_ends_the_run_canceled()
        {
            var (argsPath, output) = Prepare(seconds: 5);

            using var runner = JobRunner.ForVideoRender(argsPath, HoldForQuit);
            var run = runner.RunAsync();
            await runner.CancelAsync().WaitAsync(Bound);
            var result = await run.WaitAsync(Bound);

            Assert.Equal(JobOutcome.Canceled, result.Outcome);
            Assert.False(File.Exists(output));
        }
    }
}
