using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Prisc.Ipc.Tests
{
    /// <summary>
    /// The generated codecs against the catalog samples (HELPER_PROTOCOL.md §6). Rust round-trips each
    /// sample byte for byte; here each is decoded and re-encoded and the two compared as JSON values,
    /// because Utf8JsonWriter writes serde_json's <c>30.0</c> as <c>30</c>.
    /// </summary>
    public class HelperMessagesTests
    {
        [Fact]
        public void Every_sample_round_trips_through_the_generated_codec()
        {
            var failures = new List<string>();
            foreach (var sample in HelperSamples.All)
            {
                string encoded = null;
                try
                {
                    encoded = sample.RoundTrip(sample.Line);
                    if (encoded != null && JsonNode.DeepEquals(JsonNode.Parse(sample.Line), JsonNode.Parse(encoded)))
                        continue;
                }
                catch (System.Exception e) when (e is JsonException or System.ArgumentException)
                {
                    encoded = e.Message;
                }
                failures.Add($"{sample.Catalog} {sample.Name}:\n  sample  {sample.Line}\n  encoded {encoded}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_catalog_has_samples_for_its_events_commands_and_error()
        {
            var catalogs = HelperSamples.All.Select(s => s.Catalog).Distinct().ToArray();
            Assert.Equal(["common", "recorder", "share_region", "job", "scroll", "capture"], catalogs);
            foreach (var catalog in catalogs.Skip(1))
                Assert.Contains(HelperSamples.All, s => s.Catalog == catalog && s.Name == "event error");
        }

        [Fact]
        public void Floats_compare_by_value_and_non_ascii_goes_out_literally()
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[-100.0,30.0]"), JsonNode.Parse("[-100,30]")));

            var line = RecorderCodec.EncodeEvent(new RecorderEvent.Cameras([new RecorderCamera("id", "Caméra <HD> & 'x' +1")]));
            Assert.Equal("""{"type":"cameras","cameras":[{"id":"id","name":"Caméra <HD> & 'x' +1"}]}""", line);
        }

        [Fact]
        public void Unknown_types_decode_to_null_and_unknown_strings_to_Unknown()
        {
            Assert.Null(JobCodec.DecodeEvent("""{"type":"hello","protocol":1}"""));
            Assert.Null(JobCodec.DecodeCommand("""{"type":"quit"}"""));
            var done = Assert.IsType<JobEvent.Done>(JobCodec.DecodeEvent("""{"result":"exploded","type":"done","extra":[1,{"a":2}]}"""));
            Assert.Equal(JobResult.Unknown, done.Result);
            Assert.Null(done.Path);
            Assert.Null(done.Bytes);
            Assert.ThrowsAny<JsonException>(() => JobCodec.DecodeEvent("not json"));
            Assert.Throws<JsonException>(() => JobCodec.DecodeEvent("""{"type":"progress","percent":"half"}"""));
            Assert.Throws<JsonException>(() => JobCodec.DecodeEvent("""{"type":"progress","percent":-1}"""));
        }

        [Fact]
        public void Missing_and_null_fields_read_the_same_and_options_are_omitted()
        {
            var a = Assert.IsType<ScrollEvent.Status>(ScrollCodec.DecodeEvent("""{"type":"status","frames":1,"height_px":2,"state":"paused"}"""));
            var b = Assert.IsType<ScrollEvent.Status>(ScrollCodec.DecodeEvent("""{"type":"status","frames":1,"height_px":2,"state":"paused","resume_in_s":null}"""));
            Assert.Equal(a, b);
            Assert.Null(a.ResumeInS);
            Assert.Equal("""{"type":"done","result":"cancelled"}""", JobCodec.EncodeEvent(new JobEvent.Done(JobResult.Cancelled, null, null)));
        }

        [Fact]
        public void Aliases_decode_only()
        {
            var capture = Assert.IsType<CaptureCommand.Capture>(CaptureCodec.DecodeCommand("""{"type":"capture","mode":"monitor"}"""));
            Assert.Equal(CaptureMode.Screen, capture.Mode);
            Assert.Equal("""{"type":"capture","mode":"screen"}""", CaptureCodec.EncodeCommand(capture));
        }

        [Fact]
        public void Specs_carry_the_generated_hashes_and_terminal_rules()
        {
            var hashes = new[] { HelperSpecs.Recorder.CatalogHash, HelperSpecs.ShareRegion.CatalogHash, HelperSpecs.Job.CatalogHash,
                HelperSpecs.Scroll.CatalogHash, HelperSpecs.Capture.CatalogHash };
            Assert.All(hashes, h => Assert.Matches("^[0-9a-f]{16}$", h));
            Assert.Equal(hashes.Length, hashes.Distinct().Count());

            Assert.Equal(System.TimeSpan.FromSeconds(65), HelperSpecs.Recorder.QuitGrace);
            Assert.True(HelperSpecs.Recorder.IsTerminal(new RecorderEvent.Cameras([])));
            Assert.False(HelperSpecs.Recorder.IsTerminal(new RecorderEvent.Initialized()));
            Assert.True(HelperSpecs.Job.IsTerminal(new JobEvent.Error(IpcErrorCodes.Failed, "boom", true)));
            Assert.False(HelperSpecs.Job.IsTerminal(new JobEvent.Error(IpcErrorCodes.BadCommand, "huh", false)));
        }

        [Fact]
        public void Hello_and_quit()
        {
            var hello = IpcCodec.DecodeHello("""{"type":"hello","protocol":1,"catalog":"job","catalog_hash":"0123456789abcdef","helper":"prisc_vid2gif","version":"1.0","pid":7}""");
            Assert.Equal(new IpcHello(1, "job", "0123456789abcdef", "prisc_vid2gif", "1.0", 7), hello);
            Assert.Null(IpcCodec.DecodeHello(IpcCodec.QuitLine));
            Assert.Equal("quit", IpcCodec.ReadType(IpcCodec.QuitLine));
        }
    }
}
