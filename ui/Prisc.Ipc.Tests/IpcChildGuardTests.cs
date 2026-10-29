using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Ipc.Tests
{
    /// <summary>
    /// <see cref="IpcChild"/> in a real child process: this test assembly run again with one test
    /// selected and a role in the environment (the way prisc_ipc's guard tests respawn themselves).
    /// The guard takes the process's stdout, so it cannot run inside the test host.
    /// </summary>
    public sealed class IpcChildGuardTests
    {
        private const string RoleVariable = "PRISC_IPC_CHILD_TEST_ROLE";
        private const string ChildTest = "Prisc.Ipc.Tests.IpcChildGuardTests.Child_role";
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

        /// <summary>The child side; does nothing unless the role variable is set.</summary>
        [Fact]
        public void Child_role()
        {
            switch (Environment.GetEnvironmentVariable(RoleVariable))
            {
                case "guard":
                {
                    var events = IpcChild.Open("prisc_ipc_test_child", HelperSpecs.Job);
                    // a native write straight to fd 1 (STD_OUTPUT_HANDLE on Windows), then a managed one
                    using (var fd1 = OperatingSystem.IsWindows()
                               ? Console.OpenStandardOutput()
                               : new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, 1))
                    {
                        fd1.Write(Encoding.UTF8.GetBytes("NATIVE-STRAY\n"));
                        fd1.Flush();
                    }
                    Console.WriteLine("MANAGED-STRAY");
                    Console.Out.Flush();
                    events.Emit(new JobEvent.Progress(42));
                    Environment.Exit(0);
                    break;
                }
                case "quit":
                {
                    var events = IpcChild.Open("prisc_ipc_test_child", HelperSpecs.Job);
                    using var quit = new ManualResetEventSlim();
                    IpcChild.OnQuit += () => quit.Set();
                    IpcChild.ReadCommands(JobCodec.DecodeCommand, _ => { }, _ => { });
                    // ready: the parent quits us (a quit line, or a signal) once it reads this
                    events.Emit(new JobEvent.Progress(1));
                    quit.Wait();
                    events.Emit(new JobEvent.Done(JobResult.Cancelled, null, null));
                    Environment.Exit(HelperExit.Ok);
                    break;
                }
            }
        }

        private sealed record ChildRun(int ExitCode, List<string> Stdout, string Stderr);

        /// <summary>Starts the child role; <paramref name="drive"/> gets the process and the stdout
        /// lines read so far (after <c>hello</c>, anything the test runner printed before it
        /// dropped), and returns once it has done its part.</summary>
        private static ChildRun RunChild(string role, Action<Process, Func<string>> drive = null)
        {
            var assembly = typeof(IpcChildGuardTests).Assembly.Location;
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(assembly),
            };
            foreach (var arg in new[] { "exec", assembly, "-method", ChildTest, "-noLogo", "-noColor" })
                psi.ArgumentList.Add(arg);
            psi.Environment[RoleVariable] = role;

            using var process = Process.Start(psi);
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            var before = new List<string>();
            bool helloSeen = false;
            string Next()
            {
                while (true)
                {
                    var line = process.StandardOutput.ReadLine();
                    if (line == null)
                        return null;
                    if (!helloSeen)
                    {
                        // the in-process runner may print before the child claims stdout
                        if (TryDecodeHello(line) is not { } hello)
                        {
                            before.Add(line);
                            continue;
                        }
                        Assert.Equal(HelperSpecs.Job.CatalogHash, hello.CatalogHash);
                        helloSeen = true;
                    }
                    lines.Add(line);
                    return line;
                }
            }

            drive?.Invoke(process, Next);
            while (Next() != null)
            {
            }
            Assert.True(process.WaitForExit((int)Generous.TotalMilliseconds), "the child did not exit");
            var err = stderr.GetAwaiter().GetResult();
            Assert.True(helloSeen, "no hello; stdout: " + String.Join(" | ", before) + "\nstderr: " + err);
            return new ChildRun(process.ExitCode, lines, err);
        }

        private static IpcHello TryDecodeHello(string line)
        {
            try
            {
                return IpcCodec.DecodeHello(line);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        [Fact]
        public void Fd1_and_console_out_reach_stderr_while_protocol_lines_reach_stdout()
        {
            InteropTestGate.Require();
            var run = RunChild("guard");

            Assert.Equal(0, run.ExitCode);
            Assert.True(run.Stdout.Count == 2, "stdout after hello: " + String.Join(" | ", run.Stdout) + "\nstderr: " + run.Stderr);
            Assert.NotNull(IpcCodec.DecodeHello(run.Stdout[0]));
            Assert.Equal(new JobEvent.Progress(42), JobCodec.DecodeEvent(run.Stdout[1]));
            Assert.DoesNotContain(run.Stdout, l => l.Contains("STRAY", StringComparison.Ordinal));
            Assert.Contains("NATIVE-STRAY", run.Stderr, StringComparison.Ordinal);
            Assert.Contains("MANAGED-STRAY", run.Stderr, StringComparison.Ordinal);
        }

        private static void AssertQuitLikeEnding(ChildRun run)
        {
            Assert.Equal(HelperExit.Ok, run.ExitCode);
            // The in-process test runner answers SIGINT with a "Cancelling..." line through the
            // console writer it opened before the child claimed stdout: a descriptor of the pipe made
            // before the guard, which no guard can take back (why Open must come first in Main). It
            // is not the child's output, so only protocol lines count here; the guard test above
            // holds stdout to protocol lines only.
            var events = run.Stdout.Skip(1).Where(l => l.StartsWith('{')).Select(JobCodec.DecodeEvent).ToList();
            Assert.True(events.Count == 2, "events: " + String.Join(" | ", events) + "\nstderr: " + run.Stderr);
            Assert.Equal(new JobEvent.Progress(1), events[0]);
            Assert.Equal(JobResult.Cancelled, Assert.IsType<JobEvent.Done>(events[1]).Result);
        }

        [Fact]
        public void A_quit_line_ends_the_child_through_on_quit()
        {
            InteropTestGate.Require();
            var run = RunChild("quit", (process, next) =>
            {
                Assert.NotNull(next()); // hello
                Assert.NotNull(next()); // ready
                process.StandardInput.WriteLine(IpcCodec.QuitLine);
                process.StandardInput.Flush();
            });
            AssertQuitLikeEnding(run);
        }

        [Theory]
        [InlineData("TERM")]
        [InlineData("INT")]
        [InlineData("HUP")]
        public void A_termination_signal_ends_the_child_like_a_quit_line(string signal)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "no POSIX signals to send on Windows");
            InteropTestGate.Require();
            var run = RunChild("quit", (process, next) =>
            {
                Assert.NotNull(next()); // hello
                Assert.NotNull(next()); // ready
                using var kill = Process.Start(new ProcessStartInfo("/bin/kill", $"-{signal} {process.Id}") { UseShellExecute = false });
                Assert.True(kill.WaitForExit((int)Generous.TotalMilliseconds) && kill.ExitCode == 0, "kill failed");
            });
            AssertQuitLikeEnding(run);
            Assert.Contains("ipc: quitting (SIG" + signal + ")", run.Stderr, StringComparison.Ordinal);
        }
    }
}
