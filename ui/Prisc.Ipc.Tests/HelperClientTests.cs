using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Prisc.Ipc.Tests
{
    /// <summary>
    /// <see cref="HelperClient{TEvent, TCommand}"/> over an in-memory child (HELPER_PROTOCOL.md §7.2):
    /// the handshake, reply matching that never steals from <see cref="HelperClient{TEvent, TCommand}.Events"/>,
    /// refusals failing a wait, the bounded drain, and the outcome mapping.
    /// </summary>
    public class HelperClientTests
    {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

        private static string Hello(HelperSpec<RecorderEvent, RecorderCommand> spec, string hash = null) =>
            IpcCodec.EncodeHello(new IpcHello(IpcProtocol.Version, spec.Catalog, hash ?? spec.CatalogHash, "prisc_recorder", "test", 1));

        private static async Task<(HelperClient<RecorderEvent, RecorderCommand> Client, FakeChild Child)> StartRecorderAsync(
            Action<FakeChild> beforeHello = null, TimeSpan? quitGrace = null)
        {
            var child = new FakeChild();
            beforeHello?.Invoke(child);
            child.WriteStdout(Hello(HelperSpecs.Recorder));
            var client = new HelperClient<RecorderEvent, RecorderCommand>(HelperSpecs.Recorder, child.ToHelperChild(), null)
            {
                PumpDrainTimeout = Short,
                KillWait = Short,
                QuitGrace = quitGrace ?? Short,
            };
            await HelperClient<RecorderEvent, RecorderCommand>.StartAsync(client, "prisc_recorder", default).WaitAsync(Generous);
            return (client, child);
        }

        private static async Task<List<RecorderEvent>> DrainAsync(HelperClient<RecorderEvent, RecorderCommand> client)
        {
            var events = new List<RecorderEvent>();
            await foreach (var ev in client.Events.ReadAllAsync().WithCancellation(new CancellationTokenSource(Generous).Token))
                events.Add(ev);
            return events;
        }

        [Fact]
        public async Task Start_completes_on_a_matching_hello()
        {
            var (client, child) = await StartRecorderAsync();
            Assert.Equal("prisc_recorder", client.Hello.Helper);
            Assert.Equal(HelperSpecs.Recorder.CatalogHash, client.Hello.CatalogHash);
            child.Exit(0);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task A_stale_catalog_hash_fails_the_start_and_kills_the_child()
        {
            var child = new FakeChild();
            child.WriteStdout(Hello(HelperSpecs.Recorder, hash: "0000000000000000"));
            var client = new HelperClient<RecorderEvent, RecorderCommand>(HelperSpecs.Recorder, child.ToHelperChild(), null)
            {
                PumpDrainTimeout = Short,
                KillWait = Short,
                QuitGrace = Short,
            };

            var ex = await Assert.ThrowsAsync<HelperStartException>(() =>
                HelperClient<RecorderEvent, RecorderCommand>.StartAsync(client, "prisc_recorder", default).WaitAsync(Generous));
            Assert.Equal(HelperFailure.ProtocolMismatch, ex.Failure);
            Assert.True(child.Killed);
        }

        [Fact]
        public async Task An_exit_before_hello_maps_its_exit_code()
        {
            var child = new FakeChild();
            var client = new HelperClient<CaptureEvent, CaptureCommand>(HelperSpecs.Capture, child.ToHelperChild(), null)
            {
                PumpDrainTimeout = Short,
                KillWait = Short,
                QuitGrace = Short,
            };
            child.Exit(HelperExit.NoScreenPermission);

            var ex = await Assert.ThrowsAsync<HelperStartException>(() =>
                HelperClient<CaptureEvent, CaptureCommand>.StartAsync(client, "prisc_capture", default).WaitAsync(Generous));
            Assert.Equal(HelperFailure.NoScreenPermission, ex.Failure);
            Assert.Equal(HelperExit.NoScreenPermission, ex.ExitCode);
        }

        [Fact]
        public async Task No_hello_in_time_is_a_hello_timeout()
        {
            var child = new FakeChild();
            var client = new HelperClient<JobEvent, JobCommand>(HelperSpecs.Job, child.ToHelperChild(),
                new HelperStartOptions { HelloTimeout = Short })
            {
                PumpDrainTimeout = Short,
                KillWait = Short,
                QuitGrace = Short,
            };

            var ex = await Assert.ThrowsAsync<HelperStartException>(() =>
                HelperClient<JobEvent, JobCommand>.StartAsync(client, "prisc_vid2gif", default).WaitAsync(Generous));
            Assert.Equal(HelperFailure.HelloTimeout, ex.Failure);
            Assert.True(child.Killed);
        }

        [Fact]
        public async Task A_reply_completes_the_wait_and_is_still_delivered_in_order()
        {
            var (client, child) = await StartRecorderAsync();
            child.OnCommand = line =>
            {
                if (line.Contains("\"start\""))
                {
                    child.WriteStdout("""{"type":"levels","speaker":[-12.5],"mic":[]}""");
                    child.WriteStdout("""{"type":"started_recording","tracks":{"screen":{"index":0,"width":8,"height":8},"audio":[]}}""");
                }
            };

            var reply = await client.SendAndWaitAsync(new RecorderCommand.Start(),
                e => e is RecorderEvent.StartedRecording, Generous);
            Assert.IsType<RecorderEvent.StartedRecording>(reply);

            child.WriteStdout("""{"type":"done","result":"stopped","code":0,"message":"ok"}""");
            child.Exit(0);
            var events = await DrainAsync(client);

            Assert.Collection(events,
                e => Assert.IsType<RecorderEvent.Levels>(e),
                e => Assert.Same(reply, e),
                e => Assert.IsType<RecorderEvent.Done>(e));
            Assert.Equal("{\"type\":\"start\"}", child.Commands[0]);

            var outcome = await client.Completion.WaitAsync(Generous);
            Assert.Equal(HelperFailure.None, outcome.Failure);
            Assert.IsType<RecorderEvent.Done>(outcome.Terminal);
        }

        [Fact]
        public async Task A_refusal_fails_the_wait_and_is_still_delivered()
        {
            var (client, child) = await StartRecorderAsync();
            child.OnCommand = _ =>
                child.WriteStdout("""{"type":"error","code":"rejected","message":"not started yet","fatal":false}""");

            var ex = await Assert.ThrowsAsync<HelperCommandException>(() =>
                client.SendAndWaitAsync(new RecorderCommand.Pause(), e => e is RecorderEvent.RecordingPaused, Generous));
            Assert.Equal(IpcErrorCodes.Rejected, ex.Error.Code);

            // a refusal never changes state: no terminal, and the next wait works
            child.OnCommand = _ => child.WriteStdout("""{"type":"configure_applied","ignored_keys":[]}""");
            await client.SendAndWaitAsync(new RecorderCommand.Configure("/tmp/s.json"),
                e => e is RecorderEvent.ConfigureApplied or RecorderEvent.ConfigureError, Generous);

            child.Exit(0);
            var events = await DrainAsync(client);
            Assert.IsType<RecorderEvent.Error>(events[0]);
            Assert.IsType<RecorderEvent.ConfigureApplied>(events[1]);
            Assert.Equal(HelperFailure.Crashed, (await client.Completion).Failure);
        }

        [Fact]
        public async Task Only_one_wait_may_be_outstanding_and_an_exit_fails_it()
        {
            var (client, child) = await StartRecorderAsync();

            var first = client.SendAndWaitAsync(new RecorderCommand.Start(), e => e is RecorderEvent.StartedRecording, Generous);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendAndWaitAsync(new RecorderCommand.Pause(), e => e is RecorderEvent.RecordingPaused, Generous));

            child.Exit(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Generous));
        }

        [Fact]
        public async Task A_fatal_error_is_the_terminal_and_maps_to_FatalError()
        {
            var (client, child) = await StartRecorderAsync();
            child.WriteStdout("""{"type":"error","code":"init_failed","message":"libobs failed","fatal":true}""");
            child.Exit(1);

            var outcome = await client.Completion.WaitAsync(Generous);
            Assert.Equal(HelperFailure.FatalError, outcome.Failure);
            Assert.Equal("libobs failed", outcome.Error.Message);
            Assert.Equal(1, outcome.ExitCode);
        }

        [Fact]
        public async Task An_unknown_error_code_is_treated_as_fatal()
        {
            var (client, child) = await StartRecorderAsync();
            child.WriteStdout("""{"type":"error","code":"from_the_future","message":"?","fatal":false}""");
            child.Exit(0);

            Assert.Equal(HelperFailure.FatalError, (await client.Completion.WaitAsync(Generous)).Failure);
        }

        [Theory]
        [InlineData(0, HelperFailure.Crashed)]
        [InlineData(1, HelperFailure.Crashed)]
        [InlineData(HelperExit.NoScreenPermission, HelperFailure.NoScreenPermission)]
        [InlineData(HelperExit.CaptureFailed, HelperFailure.CaptureFailed)]
        [InlineData(HelperExit.InferenceUnavailable, HelperFailure.InferenceUnavailable)]
        public async Task An_exit_without_a_terminal_maps_its_code(int exitCode, HelperFailure expected)
        {
            var (client, child) = await StartRecorderAsync();
            child.WriteStdout("""{"type":"initialized"}""");
            child.Exit(exitCode);

            var outcome = await client.Completion.WaitAsync(Generous);
            Assert.Equal(expected, outcome.Failure);
            Assert.Null(outcome.Terminal);
        }

        [Fact]
        public async Task Stop_sends_quit_closes_stdin_and_reports_the_terminal()
        {
            var (client, child) = await StartRecorderAsync(quitGrace: Generous);
            child.OnCommand = line =>
            {
                if (line == IpcCodec.QuitLine)
                {
                    child.WriteStdout("""{"type":"done","result":"cancelled","code":0,"message":"ok"}""");
                    child.Exit(0);
                }
            };

            var outcome = await client.StopAsync().WaitAsync(Generous);
            Assert.Equal(HelperFailure.None, outcome.Failure);
            Assert.True(child.StdinClosed);
            Assert.False(child.Killed);
            Assert.False(client.Send(new RecorderCommand.Start()));
            Assert.Same(await client.StopAsync(), outcome);
        }

        [Fact]
        public async Task A_stop_that_has_to_kill_is_a_timeout_whatever_arrives_afterwards()
        {
            var (client, child) = await StartRecorderAsync();
            child.OnKill = () =>
            {
                child.WriteStdout("""{"type":"done","result":"stopped","code":0,"message":"ok"}""");
                child.Exit(-9);
            };

            var outcome = await client.StopAsync().WaitAsync(Generous);
            Assert.True(child.Killed);
            Assert.Equal(HelperFailure.Timeout, outcome.Failure);
        }

        [Fact]
        public async Task A_held_open_pipe_does_not_hang_completion()
        {
            var (client, child) = await StartRecorderAsync();
            child.WriteStdout("""{"type":"done","result":"stopped","code":0,"message":"ok"}""");
            // the process exits, but a grandchild still holds stdout and stderr open
            child.Exit(0, closeStreams: false);

            var outcome = await client.Completion.WaitAsync(Generous);
            Assert.Equal(HelperFailure.None, outcome.Failure);

            // the event channel completed with the process, and a late line is dropped
            child.WriteStdout("""{"type":"initialized"}""");
            var events = await DrainAsync(client);
            Assert.Single(events);
        }

        [Fact]
        public async Task Non_protocol_stdout_and_stderr_go_to_the_log()
        {
            var (client, child) = await StartRecorderAsync();
            child.WriteStdout("not json at all");
            child.WriteStdout("""{"type":"from_the_future"}""");
            child.WriteStderr("libobs: hello from stderr");
            child.WriteStdout("""{"type":"done","result":"stopped","code":0,"message":"ok"}""");
            child.Exit(0);

            var outcome = await client.Completion.WaitAsync(Generous);
            Assert.Contains("not json at all", outcome.StderrTail);
            Assert.Contains("from_the_future", outcome.StderrTail);
            Assert.Contains("libobs: hello from stderr", outcome.StderrTail);
            Assert.Single(await DrainAsync(client));
        }

        [Fact]
        public async Task An_oversize_stdout_line_is_logged_and_skipped()
        {
            var child = new FakeChild();
            child.WriteStdout(Hello(HelperSpecs.Recorder));
            var client = new HelperClient<RecorderEvent, RecorderCommand>(HelperSpecs.Recorder, child.ToHelperChild(), null)
            {
                PumpDrainTimeout = Short,
                KillWait = Short,
                QuitGrace = Short,
                MaxStdoutLineBytes = 1024,
            };
            await HelperClient<RecorderEvent, RecorderCommand>.StartAsync(client, "prisc_recorder", default).WaitAsync(Generous);

            child.WriteStdout(new string('x', 20_000));
            child.WriteStdout("""{"type":"done","result":"stopped","code":0,"message":"ok"}""");
            child.Exit(0);

            var outcome = await client.Completion.WaitAsync(Generous);
            Assert.Contains("[stdout] oversize line skipped", outcome.StderrTail);
            Assert.DoesNotContain("xxxx", outcome.StderrTail);
            Assert.IsType<RecorderEvent.Done>(Assert.Single(await DrainAsync(client)));
            Assert.Equal(HelperFailure.None, outcome.Failure);
        }

        [Fact]
        public async Task The_line_reader_strips_carriage_returns_and_keeps_an_unterminated_last_line()
        {
            var reader = new BoundedLineReader(new StringReader("one\r\n\ntwo"));
            Assert.Equal(("one", false), await reader.ReadLineAsync());
            Assert.Equal(("", false), await reader.ReadLineAsync());
            Assert.Equal(("two", false), await reader.ReadLineAsync());
            Assert.Null(await reader.ReadLineAsync());
        }

        /// <summary>A child process made of channels: lines written to its stdout and stderr, commands
        /// read from its stdin, an exit the test decides, and a kill it observes.</summary>
        private sealed class FakeChild
        {
            private readonly LineReader _stdout = new();
            private readonly LineReader _stderr = new();
            private readonly TaskCompletionSource<int?> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CommandWriter _stdin;

            public FakeChild() => _stdin = new CommandWriter(this);

            public List<string> Commands { get; } = new();
            public Action<string> OnCommand { get; set; }
            public Action OnKill { get; set; }
            public volatile bool Killed;
            public bool StdinClosed => _stdin.Closed;

            public void WriteStdout(string line) => _stdout.Write(line);
            public void WriteStderr(string line) => _stderr.Write(line);

            public void Exit(int code, bool closeStreams = true)
            {
                if (closeStreams)
                {
                    _stdout.Close();
                    _stderr.Close();
                }
                _exit.TrySetResult(code);
            }

            public HelperChild ToHelperChild() => new()
            {
                ProcessId = 4242,
                Stdout = _stdout,
                Stderr = _stderr,
                Stdin = _stdin,
                Exit = _exit.Task,
                Kill = () =>
                {
                    Killed = true;
                    if (OnKill != null)
                        OnKill();
                    else
                        Exit(-9);
                },
            };

            private sealed class CommandWriter : TextWriter
            {
                private readonly FakeChild _owner;
                private readonly System.Text.StringBuilder _line = new();

                public CommandWriter(FakeChild owner) => _owner = owner;

                public bool Closed { get; private set; }

                public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

                public override void Write(char value)
                {
                    if (Closed)
                        throw new IOException("stdin is closed");
                    if (value == '\r')
                        return;
                    if (value != '\n')
                    {
                        _line.Append(value);
                        return;
                    }

                    var line = _line.ToString();
                    _line.Clear();
                    lock (_owner.Commands)
                        _owner.Commands.Add(line);
                    _owner.OnCommand?.Invoke(line);
                }

                public override void Close() => Closed = true;

                protected override void Dispose(bool disposing) => Closed = true;
            }

            private sealed class LineReader : TextReader
            {
                private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

                public void Write(string line) => _lines.Writer.TryWrite(line);

                public override void Close() => _lines.Writer.TryComplete();

                public override string ReadLine() => ReadLineAsync().GetAwaiter().GetResult();

                public override Task<string> ReadLineAsync() => ReadLineAsync(default).AsTask();

                public override async ValueTask<string> ReadLineAsync(CancellationToken cancellationToken)
                {
                    try
                    {
                        return await _lines.Reader.ReadAsync(cancellationToken);
                    }
                    catch (ChannelClosedException)
                    {
                        return null;
                    }
                }

                // the stdout pump reads characters, not lines: hand out each line with its newline
                private string _pending = "";

                public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
                {
                    if (_pending.Length == 0)
                    {
                        var line = await ReadLineAsync(cancellationToken);
                        if (line == null)
                            return 0;
                        _pending = line + "\n";
                    }

                    var count = Math.Min(buffer.Length, _pending.Length);
                    _pending.AsSpan(0, count).CopyTo(buffer.Span);
                    _pending = _pending.Substring(count);
                    return count;
                }
            }
        }
    }
}
