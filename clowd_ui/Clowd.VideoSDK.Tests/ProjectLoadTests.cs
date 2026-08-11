using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The multi-track editor's way into a recording: <see cref="VideoEditPersistence.LoadOrCreate"/>
    /// (Clowd.Ui exposes its internals to this project) and the autosave writer the session hands
    /// its bytes to. No Avalonia and no FFmpeg — the loader is JSON, the filesystem and the same
    /// keep-segment math the single-row editor used.
    ///
    /// The v1 cases pin the migration against concrete expected values (keep segments, ids/groups,
    /// the pixel-rounded webcam placement) so a migrated edit keeps composing what it rendered —
    /// these were originally proven equal to the retired single-row editor's own projection.
    /// </summary>
    public class ProjectLoadTests
    {
        private const long Ms = TimeSpan.TicksPerMillisecond;
        private const long DurationMs = 10_000;
        private const string VideoPath = @"C:\recordings\video.mp4";

        // A real videoedit.json written by the shipped v1 editor (session_20260810_162448_241.0),
        // copied in verbatim so the migration is tested against the actual shipped format.
        private const string RealV1Json = """
            {
              "Version": 1,
              "TrimStartMs": 2151,
              "TrimEndMs": 0,
              "WebcamEnabled": false,
              "WebcamShape": "Circle",
              "WebcamCornerRadius": 0.25,
              "WebcamCenterX": 0.82,
              "WebcamCenterY": 0.78,
              "WebcamWidth": 0.2,
              "Cuts": []
            }
            """;

        // A v1 file exercising everything the migration has to carry: a trim on both ends, a cut
        // inside it, and a visible rounded-rect overlay somewhere other than the default corner.
        private const string EditedV1Json = """
            {
              "Version": 1,
              "TrimStartMs": 1000,
              "TrimEndMs": 9000,
              "WebcamEnabled": true,
              "WebcamShape": "RoundedRect",
              "WebcamCornerRadius": 0.3,
              "WebcamCenterX": 0.7,
              "WebcamCenterY": 0.65,
              "WebcamWidth": 0.25,
              "Cuts": [ { "StartMs": 3000, "EndMs": 4500 } ]
            }
            """;

        // ------------------------------------------------------------------ fixtures

        private static MediaProbeResult Probe(bool withWebcam = true, int audioStreams = 1) =>
            new MediaProbeResult
            {
                Path = VideoPath,
                DurationTicks = DurationMs * Ms,
                VideoStreams = withWebcam
                    ? new[] { ScreenStream(), WebcamStream() }
                    : new[] { ScreenStream() },
                AudioStreams = Enumerable.Range(0, audioStreams)
                                         .Select(i => new AudioStreamProbe
                                         {
                                             StreamIndex = 2 + i,
                                             // different rates so the output rate is visibly the max
                                             SampleRate = i == 0 ? 48_000 : 44_100,
                                             Channels = 2,
                                             DurationTicks = DurationMs * Ms,
                                         })
                                         .ToArray(),
                HasAudio = audioStreams > 0,
            };

        private static VideoStreamProbe ScreenStream() => new VideoStreamProbe
        {
            StreamIndex = 0,
            Width = 1920,
            Height = 1080,
            AvgFrameRateNum = 30,
            AvgFrameRateDen = 1,
            RFrameRateNum = 30,
            RFrameRateDen = 1,
            DurationTicks = DurationMs * Ms,
        };

        private static VideoStreamProbe WebcamStream() => new VideoStreamProbe
        {
            StreamIndex = 1,
            Width = 640,
            Height = 480,
            AvgFrameRateNum = 30,
            AvgFrameRateDen = 1,
            RFrameRateNum = 30,
            RFrameRateDen = 1,
            DurationTicks = DurationMs * Ms,
        };

        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "clowd-projectload-" + Guid.NewGuid().ToString("N") + ".json");

        private static string WriteTemp(string json)
        {
            var path = TempPath();
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>Everything about a project except its ids, as text — ids are minted per load, so
        /// two projects describing the same edit differ only there. Rendered as a string rather than
        /// asserted field by field so a mismatch shows up as a readable diff.</summary>
        private static string Shape(Project project)
        {
            var text = new StringBuilder();
            text.AppendLine($"{project.Version}: {project.Output.WidthPx}x{project.Output.HeightPx} " +
                            $"@{project.Output.FpsNum}/{project.Output.FpsDen} {project.Output.SampleRate}Hz");

            foreach (var source in project.Sources)
            {
                text.AppendLine($"source {source.Path} [" +
                                String.Join(", ", source.Streams.Select(s => $"{s.Index}:{s.Kind} {s.Width}x{s.Height}")) + "]");
            }

            // link groups are identities too; what matters is which items share one.
            var groups = new Dictionary<Guid, int>();
            foreach (var track in project.Tracks.OrderBy(t => t.Order))
            {
                text.AppendLine($"track {track.Order} {track.Kind} \"{track.Name}\" " +
                                $"hidden={track.Hidden} muted={track.Muted} locked={track.Locked}");

                foreach (var item in project.Items.Where(i => i.TrackId == track.Id).OrderBy(i => i.TimelineStartTicks))
                {
                    int group = 0;
                    if (item.LinkGroupId is { } id && !groups.TryGetValue(id, out group))
                        groups[id] = group = groups.Count + 1;

                    text.AppendLine($"  {item.TimelineStartTicks}+{item.DurationTicks} " +
                                    Describe(item.Content) + " " + Describe(item.Transform) + $" group={group}");
                }
            }

            return text.ToString();
        }

        private static string Describe(ItemContent content) => content switch
        {
            MediaContent media => $"stream {media.StreamIndex} in={media.SourceInTicks}",
            _ => content?.GetType().Name ?? "none",
        };

        private static string Describe(Transform t) => t == null
            ? "no transform"
            : $"x={t.X:F9} y={t.Y:F9} scale={t.Scale:F9} opacity={t.Opacity:F3} " +
              (t.Mask == null ? "no mask" : $"mask={t.Mask.Shape}/{t.Mask.CornerRadius:F3}");

        // ------------------------------------------------------------------ fresh create

        [Fact]
        public void With_no_file_the_project_is_the_whole_recording()
        {
            var project = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe());

            Assert.Empty(project.Validate());
            Assert.Equal(Project.CurrentVersion, project.Version);
            Assert.Equal(1920, project.Output.WidthPx);
            Assert.Equal(1080, project.Output.HeightPx);
            Assert.Equal(48_000, project.Output.SampleRate);

            var source = Assert.Single(project.Sources);
            Assert.Equal(VideoPath, source.Path);
            Assert.Equal(new[] { 0, 1, 2 }, source.Streams.Select(s => s.Index));

            Assert.Equal(new[] { "Screen", "Webcam", "Audio" }, project.Tracks.Select(t => t.Name));
            Assert.Equal(3, project.Items.Count);
            Assert.Single(project.Items.Select(i => i.LinkGroupId).Distinct());

            foreach (var item in project.Items)
            {
                Assert.Equal(0, item.TimelineStartTicks);
                Assert.Equal(DurationMs * Ms, item.DurationTicks);
                Assert.Equal(0, ((MediaContent)item.Content).SourceInTicks);
            }
        }

        /// <summary>A fresh edit is the identity project <see cref="RecordingProject.Build"/> makes
        /// of the whole recording — the loader adds nothing of its own.</summary>
        [Fact]
        public void A_fresh_create_matches_a_whole_recording_build()
        {
            var probe = Probe(withWebcam: false, audioStreams: 1);
            var expected = RecordingProject.Build(new RecordingProjectSpec
            {
                InputPath = VideoPath,
                Screen = ScreenStream(),
                AudioStreams = probe.AudioStreams,
                FpsNum = 30,
                FpsDen = 1,
                Segments = new[] { new KeepSegment(0, DurationMs * Ms) },
            });

            var actual = VideoEditPersistence.LoadOrCreate(null, VideoPath, probe);

            Assert.Equal(Shape(expected), Shape(actual));
        }

        /// <summary>A fresh edit of a two-stream recording opens <i>showing</i> the camera: the user
        /// recorded with one on purpose, and the row's eye toggle is how they turn it off. (v1
        /// defaulted the overlay off — its single-bar UI had no row to show a camera on.) The
        /// placement it lands with is still the v1 default, pinned through the same pixel-rounded
        /// rect the v1 render path was handed, so turning it off and on cannot invent a rect.</summary>
        [Fact]
        public void A_fresh_create_shows_the_webcam_row_with_its_default_placement()
        {
            var actual = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe());

            Assert.False(actual.Tracks.Single(t => t.Name == "Webcam").Hidden);

            var rect = VideoEditPersistence.ComputeWebcamRect(new WebcamOverlay(), 1920, 1080, 640, 480);
            var expected = RecordingProject.WebcamTransform(rect.X, rect.Y, rect.W, rect.H, 1920, 1080, null);

            var cam = actual.Items.First(i => ((MediaContent)i.Content).StreamIndex == 1).Transform;
            Assert.Equal(expected.X, cam.X, 9);
            Assert.Equal(expected.Y, cam.Y, 9);
            Assert.Equal(0.2, cam.Scale, 9);
            Assert.Equal(MaskShape.Circle, cam.Mask.Shape);
            Assert.Equal(0.25, cam.Mask.CornerRadius, 9);
        }

        /// <summary>The other half of the same rule: the fresh-create default must not leak into the
        /// v1 migration. A file that says the overlay was off still opens with the row hidden —
        /// byte-faithful, whatever a new edit of the same recording would do.</summary>
        [Fact]
        public void A_migrated_v1_file_keeps_its_own_webcam_visibility()
        {
            Assert.True(LoadFrom(RealV1Json, Probe()).Tracks.Single(t => t.Name == "Webcam").Hidden);
            Assert.False(LoadFrom(EditedV1Json, Probe()).Tracks.Single(t => t.Name == "Webcam").Hidden);
        }

        [Fact]
        public void A_missing_corrupt_or_future_file_starts_a_fresh_edit()
        {
            var whole = Shape(VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe()));

            foreach (var json in new[] { "", "{ not json", """{ "Version": 99, "TrimStartMs": 500 }""" })
            {
                var path = WriteTemp(json);
                try
                {
                    Assert.Equal(whole, Shape(VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe())));
                }
                finally
                {
                    File.Delete(path);
                }
            }

            // …and so does a path that is not there at all
            Assert.Equal(whole, Shape(VideoEditPersistence.LoadOrCreate(TempPath(), VideoPath, Probe())));
        }

        // ------------------------------------------------------------------ v1 migration

        [Fact]
        public void A_real_v1_file_migrates_to_the_project_it_describes()
        {
            var probe = Probe();
            var project = LoadFrom(RealV1Json, probe);

            Assert.Empty(project.Validate());
            Assert.Equal(Project.CurrentVersion, project.Version);
            Assert.Equal(1920, project.Output.WidthPx);
            Assert.Equal(1080, project.Output.HeightPx);
            Assert.Equal(48_000, project.Output.SampleRate);

            // one source, three rows, one link group
            var source = Assert.Single(project.Sources);
            Assert.Equal(VideoPath, source.Path);
            Assert.Equal(new[] { 0, 1, 2 }, source.Streams.Select(s => s.Index).ToArray());
            Assert.Equal(new[] { "Screen", "Webcam", "Audio" }, project.Tracks.Select(t => t.Name));
            Assert.Single(project.Items.Select(i => i.LinkGroupId).Distinct());

            // the trim really is one item per row, 2151ms into the source and running to the end
            var screen = Assert.Single(project.Items, i => ((MediaContent)i.Content).StreamIndex == 0);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal((DurationMs - 2151) * Ms, screen.DurationTicks);
            Assert.Equal(2151 * Ms, ((MediaContent)screen.Content).SourceInTicks);

            // "webcam overlay off" hides the row rather than dropping it, so the placement
            // survives turning it back on.
            Assert.True(project.Tracks.Single(t => t.Name == "Webcam").Hidden);
            var cam = Assert.Single(project.Items, i => ((MediaContent)i.Content).StreamIndex == 1);
            Assert.Equal(screen.TimelineStartTicks, cam.TimelineStartTicks);
            Assert.Equal(screen.DurationTicks, cam.DurationTicks);
            Assert.Equal(MaskShape.Circle, cam.Transform.Mask.Shape);
        }

        /// <summary>The discriminating migration: keep segments around a cut, and the webcam
        /// transform derived from the pixel rect the v1 render path was handed.</summary>
        [Fact]
        public void A_trimmed_cut_and_placed_v1_file_migrates_unchanged()
        {
            var probe = Probe();
            var project = LoadFrom(EditedV1Json, probe);

            Assert.Empty(project.Validate());

            // [1000,3000) + [4500,9000) of source, placed back to back
            var screen = project.Items.Where(i => ((MediaContent)i.Content).StreamIndex == 0)
                                .OrderBy(i => i.TimelineStartTicks)
                                .ToList();
            Assert.Equal(2, screen.Count);
            Assert.Equal((0L, 2_000 * Ms, 1_000 * Ms),
                (screen[0].TimelineStartTicks, screen[0].DurationTicks, ((MediaContent)screen[0].Content).SourceInTicks));
            Assert.Equal((2_000 * Ms, 4_500 * Ms, 4_500 * Ms),
                (screen[1].TimelineStartTicks, screen[1].DurationTicks, ((MediaContent)screen[1].Content).SourceInTicks));

            var cam = project.Items.First(i => ((MediaContent)i.Content).StreamIndex == 1);
            Assert.False(project.Tracks.Single(t => t.Name == "Webcam").Hidden);
            Assert.Equal(MaskShape.RoundedRect, cam.Transform.Mask.Shape);
            Assert.Equal(0.3, cam.Transform.Mask.CornerRadius, 9);
            Assert.Equal(0.25, cam.Transform.Scale, 9); // 480px of 1920, rounded on the way through
        }

        /// <summary>v1 knew of one audio stream; the migrated project gets a row per stream the
        /// file actually carries, which is what the multi-track editor mixes.</summary>
        [Fact]
        public void A_v1_file_migrates_with_every_probed_audio_stream()
        {
            var project = LoadFrom(EditedV1Json, Probe(audioStreams: 2));

            Assert.Empty(project.Validate());
            Assert.Equal(new[] { "Screen", "Webcam", "Audio 1", "Audio 2" }, project.Tracks.Select(t => t.Name));
            Assert.Equal(48_000, project.Output.SampleRate); // the highest of the two rates

            foreach (int stream in new[] { 2, 3 })
            {
                var items = project.Items.Where(i => ((MediaContent)i.Content).StreamIndex == stream).ToList();
                Assert.Equal(2, items.Count); // one per keep segment, like every other row
            }

            Assert.Single(project.Items.Select(i => i.LinkGroupId).Distinct());
        }

        [Fact]
        public void A_fresh_create_gets_a_row_per_audio_stream()
        {
            var project = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 3));

            Assert.Empty(project.Validate());
            Assert.Equal(new[] { "Audio 1", "Audio 2", "Audio 3" },
                project.Tracks.Where(t => t.Kind == TrackKind.Audio).OrderBy(t => t.Order).Select(t => t.Name));
            Assert.Equal(new[] { 2, 3, 4 },
                project.Items.Select(i => ((MediaContent)i.Content).StreamIndex).Where(s => s >= 2).Order());
        }

        /// <summary>
        /// The recorder's labels name the rows a fresh edit creates. The probe stays the authority
        /// on which rows exist: labels are index-aligned decoration, so a stream the recorder said
        /// nothing about keeps the numbered fallback, and a label with no stream is simply unused.
        /// </summary>
        [Fact]
        public void A_fresh_create_names_its_audio_rows_from_the_recorder_labels()
        {
            var names = new[] { "System Audio", "Microphone" };

            var project = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 2), names);

            Assert.Empty(project.Validate());
            Assert.Equal(new[] { "Screen", "Webcam", "System Audio", "Microphone" },
                project.Tracks.OrderBy(t => t.Order).Select(t => t.Name));

            // one label short, and one label too many: neither changes the rows themselves
            Assert.Equal(new[] { "System Audio", "Audio 2", "Audio 3" },
                AudioRowNames(VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 3), new[] { "System Audio" })));
            Assert.Equal(new[] { "System Audio" },
                AudioRowNames(VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 1), names)));

            // a gap in the labels (an unrecognized kind) falls through to the numbered fallback
            Assert.Equal(new[] { "Audio 1", "Microphone" },
                AudioRowNames(VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 2), new[] { null, "Microphone" })));

            // …and without any labels nothing changes at all
            Assert.Equal(new[] { "Audio 1", "Audio 2" },
                AudioRowNames(VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 2))));
        }

        /// <summary>A saved edit carries the names it was built with (and whatever the user renamed
        /// them to); the labels are only consulted when the project is created.</summary>
        [Fact]
        public void A_saved_edit_keeps_its_own_row_names()
        {
            var project = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 2),
                new[] { "System Audio", "Microphone" });
            project.Tracks.Single(t => t.Name == "Microphone").Name = "Commentary";

            var path = WriteTemp(project.ToJson());
            try
            {
                var reloaded = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe(audioStreams: 2),
                    new[] { "System Audio", "Microphone" });

                Assert.Equal(new[] { "System Audio", "Commentary" }, AudioRowNames(reloaded));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string[] AudioRowNames(Project project) => project.Tracks
            .Where(t => t.Kind == TrackKind.Audio)
            .OrderBy(t => t.Order)
            .Select(t => t.Name)
            .ToArray();

        // ------------------------------------------------------------------ v2 files

        [Fact]
        public void A_v2_file_reloads_as_itself()
        {
            var project = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe());
            var path = WriteTemp(project.ToJson());
            try
            {
                var reloaded = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe());

                // ids included: the player only keeps its decoders when they survive a reload
                Assert.Equal(project.ToJson(), reloaded.ToJson());
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Files written by the retired single-row editor carry an <c>EditorState</c>
        /// block beside the project's own properties. It is not part of the model, so the loader
        /// must read straight past it rather than choking on it.</summary>
        [Fact]
        public void A_v2_file_with_a_legacy_editor_block_loads_and_ignores_it()
        {
            var saved = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe());

            // splice the sidecar block in as a sibling of the project's own properties, exactly
            // where the old editor's writer put it.
            var json = saved.ToJson();
            json = json.TrimEnd().TrimEnd('}') +
                   """, "EditorState": { "Version": 1, "TrimStartMs": 8000, "TrimEndMs": 0, "Cuts": [] } }""";

            var path = WriteTemp(json);
            try
            {
                var project = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe());

                Assert.Empty(project.Validate());
                Assert.Equal(saved.ToJson(), project.ToJson()); // the block changed nothing
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Session directories are moved, copied and renamed; the recording the caller
        /// opened is the truth, whatever path the saved project remembers. Imported media is not
        /// the recording and keeps its own path.</summary>
        [Fact]
        public void The_recordings_own_source_is_pointed_back_at_the_file_being_opened()
        {
            var project = VideoEditPersistence.LoadOrCreate(null, @"D:\old-session\video.mp4", Probe());
            var import = AddImport(project, @"D:\media\overlay.mp4");
            Assert.Empty(project.Validate());

            var path = WriteTemp(project.ToJson());
            try
            {
                var reloaded = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe());

                Assert.Equal(VideoPath, reloaded.Sources.Single(s => s.Id != import).Path);
                Assert.Equal(@"D:\media\overlay.mp4", reloaded.Sources.Single(s => s.Id == import).Path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>The import flow's round trip: what <see cref="EditorSession.ImportMedia"/>
        /// builds is what a save writes and a reload gives back — ids included, so the player keeps
        /// its decoders and the items stay linked as one file across a close/reopen.</summary>
        [Fact]
        public void An_imported_file_and_its_rows_survive_a_save_and_reload()
        {
            var session = new EditorSession(
                VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe()), null, null);

            var created = session.ImportMedia(@"D:\media\overlay.mp4", new MediaProbeResult
            {
                Path = @"D:\media\overlay.mp4",
                DurationTicks = 5_000 * Ms,
                VideoStreams = new[]
                {
                    new VideoStreamProbe { StreamIndex = 0, Width = 1280, Height = 720, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = 5_000 * Ms },
                },
                AudioStreams = new[]
                {
                    new AudioStreamProbe { StreamIndex = 1, SampleRate = 48_000, Channels = 2, DurationTicks = 5_000 * Ms },
                },
                HasAudio = true,
            }, 2_000 * Ms);
            Assert.Equal(2, created.Count);

            var path = WriteTemp(session.Project.ToJson());
            try
            {
                var reloaded = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe());

                Assert.Empty(reloaded.Validate());
                Assert.Equal(session.Project.ToJson(), reloaded.ToJson());

                // the import keeps its own path (only the recording's source is reconciled), its
                // two rows and its link group.
                var import = reloaded.Sources.Single(s => s.Path == @"D:\media\overlay.mp4");
                var items = reloaded.Items.Where(i => ((MediaContent)i.Content).SourceId == import.Id).ToList();
                Assert.Equal(2, items.Count);
                Assert.Equal(2_000 * Ms, items[0].TimelineStartTicks);
                Assert.NotNull(items[0].LinkGroupId);
                Assert.Equal(items[0].LinkGroupId, items[1].LinkGroupId);

                var rows = items.Select(i => reloaded.Tracks.Single(t => t.Id == i.TrackId)).ToList();
                Assert.Contains(rows, t => t.Kind == TrackKind.Video && t.Name == "overlay");
                Assert.Contains(rows, t => t.Kind == TrackKind.Audio && t.Name == "overlay");
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Adds a second source with one video stream, on a row above the recording's —
        /// what importing media produces, hand-built so this test does not depend on the session.
        /// Returns the new source's id.</summary>
        private static Guid AddImport(Project project, string path)
        {
            var source = new Source { Id = Guid.NewGuid(), Path = path };
            source.Streams.Add(new SourceStream
            {
                Index = 0,
                Kind = StreamKind.Video,
                Width = 1280,
                Height = 720,
                DurationTicks = 5_000 * Ms,
            });
            project.Sources.Add(source);

            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Overlay", Order = 9 };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5_000 * Ms,
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0 },
                Transform = new Transform { Scale = 0.5 },
            });

            project.Normalize();
            return source.Id;
        }

        // ------------------------------------------------------------------ autosave

        [Fact]
        public void The_autosave_writes_the_newest_bytes_and_flushes_synchronously()
        {
            var path = TempPath();
            try
            {
                var autosave = new EditorAutosave(path);
                autosave.Write(Encoding.UTF8.GetBytes("one"));
                autosave.Write(Encoding.UTF8.GetBytes("two"));
                autosave.Flush();
                Assert.Equal("two", File.ReadAllText(path));

                autosave.Write(Encoding.UTF8.GetBytes("three"));
                autosave.Flush();
                Assert.Equal("three", File.ReadAllText(path));

                // nothing pending: flushing again must not truncate or rewrite anything
                autosave.Flush();
                Assert.Equal("three", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>The round trip the editor actually performs: the session serializes its project
        /// (a bare <c>ToJson</c> — no sidecar blocks), the autosave writes it, and the loader
        /// reads back the very same project, ids included.</summary>
        [Fact]
        public void A_project_the_session_saved_reloads_identically()
        {
            var project = VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 2));
            project.Items[0].Transform.Opacity = 0.5;

            var bytes = Encoding.UTF8.GetBytes(project.ToJson());

            var path = TempPath();
            try
            {
                var autosave = new EditorAutosave(path);
                autosave.Write(bytes);
                autosave.Flush();

                var reloaded = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe(audioStreams: 2));
                Assert.Equal(project.ToJson(), reloaded.ToJson());
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The whole production save path end to end, with nothing hand-serialized: a real
        /// <see cref="EditorSession"/> over a loaded project, edited the way the timeline edits it
        /// (a gesture-wrapped move, a split, a ripple delete, a track toggle, an undo), through a
        /// debouncing scheduler shaped like the window's, into a real <see cref="EditorAutosave"/>
        /// and onto disk — then read back with a bare <see cref="Project.FromJson"/>.
        ///
        /// The other autosave tests feed the writer bytes of their own making; this one proves the
        /// bytes the session itself produces after a realistic edit are a parseable, valid v2
        /// project, and that the debounce cannot lose the newest of them across a close.
        /// </summary>
        [Fact]
        public void A_session_edited_and_flushed_writes_a_file_that_parses_as_the_project()
        {
            var path = TempPath();
            try
            {
                // the window's scheduler: hold the newest callback back, run it only on flush.
                Action pending = null;
                var autosave = new EditorAutosave(path);
                var session = new EditorSession(
                    VideoEditPersistence.LoadOrCreate(null, VideoPath, Probe(audioStreams: 2)),
                    autosave, save => pending = save);

                var screenTrack = session.Project.Tracks.Single(t => t.Name == "Screen");
                var screen = session.Project.Items.Single(i => i.TrackId == screenTrack.Id);

                Assert.True(session.SplitAtPlayhead(4_000 * Ms));
                session.RippleDeleteItem(session.Project.Items
                    .Where(i => i.TrackId == screenTrack.Id)
                    .OrderBy(i => i.TimelineStartTicks)
                    .First().Id);
                session.SetTrackHidden(session.Project.Tracks.Single(t => t.Name == "Webcam").Id, false);

                // an unlinked item can be dragged; the gesture is the undo unit, as in the timeline
                var moved = session.Project.Items.First(i => i.TrackId == screenTrack.Id);
                session.UnlinkTrack(screenTrack.Id);
                using (var gesture = session.BeginGesture("Move"))
                {
                    session.MoveItem(moved.Id, 250 * Ms);
                    session.MoveItem(moved.Id, 250 * Ms);
                    gesture.Commit();
                }

                session.Undo(); // history has to survive into the saved bytes as state, not as ops

                // nothing is on disk yet — the scheduler is holding the write, like the debounce
                Assert.False(File.Exists(path));

                session.FlushSave();
                autosave.Flush();

                var written = File.ReadAllText(path);
                var reloaded = Project.FromJson(written);

                Assert.NotNull(reloaded);
                Assert.Equal(Project.CurrentVersion, reloaded.Version);
                Assert.Empty(reloaded.Validate());
                Assert.Equal(session.Project.ToJson(), reloaded.ToJson());
                Assert.Equal(session.DurationTicks, reloaded.GetDurationTicks());

                // and the editor's own way in reads it back as the same project
                Assert.Equal(session.Project.ToJson(),
                    VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe(audioStreams: 2)).ToJson());

                // the deferred callback the scheduler still holds is stale-but-harmless: running it
                // after the flush rewrites the same state rather than an older one.
                pending?.Invoke();
                autosave.Flush();
                Assert.Equal(written, File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static Project LoadFrom(string json, MediaProbeResult probe)
        {
            var path = WriteTemp(json);
            try
            {
                return VideoEditPersistence.LoadOrCreate(path, VideoPath, probe);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
