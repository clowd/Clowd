using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Prisc.Ipc.Tests
{
    /// <summary>
    /// The managed command loop (<see cref="IpcChild"/>) over the golden framing vectors that
    /// prisc_ipc's own stdin tests run (crates/prisc_ipc/src/framing.rs, generated into
    /// Generated/FramingVectors.g.cs): line splitting, the 1 MiB cap, CR and BOM stripping, lossy
    /// UTF-8, blank lines, the refusal texts, and quit-then-read. One vector, one behaviour, two loops.
    /// </summary>
    public sealed class FramingTests
    {
        public static TheoryData<string> Names()
        {
            var names = new TheoryData<string>();
            foreach (var vector in FramingVectors.All)
                names.Add(vector.Name);
            return names;
        }

        /// <summary>The vectors' test vocabulary, decoded the way a generated catalog codec decodes:
        /// null for an unknown type, an exception for a missing or invalid field.</summary>
        private static string Decode(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ping":
                    if (!root.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetUInt32(out _))
                        throw new JsonException("missing or invalid field `n`");
                    return line;
                case "echo":
                    if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                        throw new JsonException("missing or invalid field `text`");
                    return line;
                default:
                    return null;
            }
        }

        private sealed record Seen(FramingExpectKind Kind, string Text);

        private static List<Seen> Run(FramingVector vector)
        {
            var seen = new List<Seen>();
            var quit = false;
            void Quit(FramingExpectKind kind)
            {
                // the quit path fires once, as prisc_ipc's lifetime and IpcChild.TriggerQuit do
                if (quit)
                    return;
                quit = true;
                seen.Add(new Seen(kind, null));
            }

            IpcChild.Pump(new MemoryStream(vector.Bytes()), Decode,
                line => seen.Add(new Seen(FramingExpectKind.Command, line)),
                reason => seen.Add(new Seen(FramingExpectKind.Malformed, reason)),
                () => Quit(FramingExpectKind.QuitCommand));
            Quit(FramingExpectKind.QuitStdinClosed);
            return seen;
        }

        [Theory]
        [MemberData(nameof(Names))]
        public void The_managed_loop_passes_the_golden_vector(string name)
        {
            var vector = Array.Find(FramingVectors.All, v => v.Name == name);
            var seen = Run(vector);

            Assert.True(seen.Count == vector.Expected.Length,
                $"{name}: expected {vector.Expected.Length} steps, got {String.Join(" | ", seen)}");
            for (int i = 0; i < seen.Count; i++)
            {
                var expected = vector.Expected[i];
                var actual = seen[i];
                switch (expected.Kind)
                {
                    case FramingExpectKind.Command:
                        Assert.Equal(FramingExpectKind.Command, actual.Kind);
                        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.Text), JsonNode.Parse(actual.Text)),
                            $"{name}: expected {expected.Text}, got {actual.Text}");
                        break;
                    case FramingExpectKind.Malformed:
                        Assert.Equal(FramingExpectKind.Malformed, actual.Kind);
                        Assert.Equal(expected.Text, actual.Text);
                        break;
                    case FramingExpectKind.MalformedPrefix:
                        Assert.Equal(FramingExpectKind.Malformed, actual.Kind);
                        Assert.StartsWith(expected.Text, actual.Text, StringComparison.Ordinal);
                        break;
                    default:
                        Assert.Equal(expected.Kind, actual.Kind);
                        break;
                }
            }
        }

        [Fact]
        public void The_vectors_cover_every_refusal_and_quit_then_read()
        {
            Assert.Contains(FramingVectors.All, v => v.Name == "quit_then_read");
            Assert.Contains(FramingVectors.All, v => Array.Exists(v.Expected, e => e.Text == IpcProtocol.OversizeReason));
            Assert.Contains(FramingVectors.All, v => Array.Exists(v.Expected, e => e.Text == IpcProtocol.NoTypeReason));
            Assert.Equal(1024 * 1024, IpcProtocol.MaxLineBytes);
            Assert.Equal(BoundedLineReader.MaxLineBytes, IpcProtocol.MaxLineBytes);
        }
    }
}
