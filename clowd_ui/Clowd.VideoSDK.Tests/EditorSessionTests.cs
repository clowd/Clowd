using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="EditorSession"/> — the single mutation funnel. Pure model math: no FFmpeg, no
    /// filesystem (persistence is a counting stub), so every test drives the real pipeline —
    /// snapshot, mutate, normalize, validate/rollback, undo, persist, notify — end to end.
    /// </summary>
    public class EditorSessionTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The recording shape: three linked items covering [0, 10s) on Screen/Webcam/
        /// Audio rows, over one 60s source with a 2s in-point — same fixture as
        /// <see cref="TimelineOpsTests"/>.</summary>
        private static Project RecordingProject(out Item screen, out Item webcam, out Item audio)
        {
            var sourceId = Guid.NewGuid();
            var linkGroup = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            Item NewItem(Track track, int streamIndex) => new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex, SourceInTicks = Ms(2_000) },
                LinkGroupId = linkGroup,
            };

            screen = NewItem(screenTrack, 0);
            webcam = NewItem(webcamTrack, 1);
            audio = NewItem(audioTrack, 2);

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { screenTrack, webcamTrack, audioTrack },
                Items = { screen, webcam, audio },
            };
        }

        private sealed class CountingPersistence : IEditorPersistence
        {
            public int Writes;
            public byte[] Last;

            public void Write(byte[] utf8Json)
            {
                Writes++;
                Last = utf8Json;
            }

            public string LastJson => Encoding.UTF8.GetString(Last);
        }

        /// <summary>Session over the recording fixture with a counting sink and a synchronous
        /// save scheduler — every committed mutation is one <see cref="CountingPersistence"/>
        /// write.</summary>
        private static EditorSession NewSession(out Item screen, out Item webcam, out Item audio,
            out CountingPersistence persist)
        {
            persist = new CountingPersistence();
            return new EditorSession(RecordingProject(out screen, out webcam, out audio), persist, save => save());
        }

        /// <summary>A 1280x720 clip with one video and one audio stream, for import tests.</summary>
        private static MediaProbeResult ClipProbe(bool withAudio = true) => new MediaProbeResult
        {
            Path = @"C:\media\clip.mp4",
            DurationTicks = Ms(8_000),
            VideoStreams = new[]
            {
                new VideoStreamProbe { StreamIndex = 0, Width = 1280, Height = 720, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(8_000) },
            },
            AudioStreams = withAudio
                ? new[] { new AudioStreamProbe { StreamIndex = 1, SampleRate = 48_000, Channels = 2, DurationTicks = Ms(7_900) } }
                : Array.Empty<AudioStreamProbe>(),
            HasAudio = withAudio,
        };

        /// <summary>A reprobe matching the recording fixture's stream shape exactly.</summary>
        private static MediaProbeResult RecordingReprobe(string path, bool includeWebcamStream = true)
        {
            var video = new List<VideoStreamProbe>
            {
                new VideoStreamProbe { StreamIndex = 0, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
            };
            if (includeWebcamStream)
                video.Add(new VideoStreamProbe { StreamIndex = 1, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) });

            return new MediaProbeResult
            {
                Path = path,
                DurationTicks = Ms(60_000),
                VideoStreams = video,
                AudioStreams = new[] { new AudioStreamProbe { StreamIndex = 2, SampleRate = 48_000, Channels = 2, DurationTicks = Ms(60_000) } },
                HasAudio = true,
            };
        }

        private static Item Resolve(EditorSession session, Guid itemId) =>
            session.Project.Items.Single(i => i.Id == itemId);

        // -------------------------------------------------------------------- the undo round trip

        [Fact]
        public void Every_mutator_mutates_and_one_undo_restores_byte_identical_json()
        {
            var cases = new (string Name,
                Action<EditorSession, Item, Item, Item> Setup,
                Action<EditorSession, Item, Item, Item> Act)[]
            {
                ("MoveItem", null, (s, sc, wc, au) => s.MoveItem(sc.Id, Ms(1_000))),
                ("TrimItemStart", null, (s, sc, wc, au) => s.TrimItemStart(sc.Id, Ms(500))),
                ("TrimItemEnd", null, (s, sc, wc, au) => s.TrimItemEnd(sc.Id, -Ms(500))),
                ("SplitAt", null, (s, sc, wc, au) => Assert.True(s.SplitAt(sc.Id, Ms(4_000)))),
                ("SplitAtPlayhead", null, (s, sc, wc, au) => Assert.True(s.SplitAtPlayhead(Ms(4_000)))),
                ("RippleDeleteItem", null, (s, sc, wc, au) => s.RippleDeleteItem(sc.Id)),
                ("DeleteItem", null, (s, sc, wc, au) => s.DeleteItem(wc.Id)),
                ("UnlinkTrack", null, (s, sc, wc, au) => s.UnlinkTrack(wc.TrackId)),
                ("TryRelinkTrack",
                    (s, sc, wc, au) => s.UnlinkTrack(wc.TrackId),
                    (s, sc, wc, au) => Assert.True(s.TryRelinkTrack(wc.TrackId))),
                ("SetTrackHidden", null, (s, sc, wc, au) => s.SetTrackHidden(wc.TrackId, true)),
                ("SetTrackMuted", null, (s, sc, wc, au) => s.SetTrackMuted(au.TrackId, true)),
                ("SetTrackLocked", null, (s, sc, wc, au) => s.SetTrackLocked(sc.TrackId, true)),
                ("RenameTrack", null, (s, sc, wc, au) => s.RenameTrack(au.TrackId, "Microphone")),
                ("EditItem", null, (s, sc, wc, au) => s.EditItem(wc.Id, i => i.Transform.X = 0.8)),
                ("AddText", null, (s, sc, wc, au) => Assert.NotNull(s.AddText(Ms(12_000), Ms(5_000)))),
                ("AddImage", null, (s, sc, wc, au) => Assert.NotNull(s.AddImage(@"C:\media\logo.png", Ms(12_000), Ms(5_000)))),
                ("ImportMedia", null, (s, sc, wc, au) => Assert.NotEmpty(s.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), 0))),
                ("RelinkSource", null, (s, sc, wc, au) => s.RelinkSource(
                    ((MediaContent)sc.Content).SourceId, @"C:\rec\moved.mp4", RecordingReprobe(@"C:\rec\moved.mp4"))),
                ("RemoveSource", null, (s, sc, wc, au) =>
                    Assert.True(s.RemoveSource(((MediaContent)sc.Content).SourceId))),
            };

            foreach (var (name, setup, act) in cases)
            {
                var session = new EditorSession(RecordingProject(out var sc, out var wc, out var au), null, null);
                setup?.Invoke(session, sc, wc, au);

                var before = session.Project.ToJson();
                act(session, sc, wc, au);
                Assert.True(before != session.Project.ToJson(), $"{name} did not change the project.");

                session.Undo();
                Assert.True(before == session.Project.ToJson(), $"{name}: undo did not restore byte-identical JSON.");
            }
        }

        [Fact]
        public void Undo_and_redo_replace_the_project_instance_and_raise_structural()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var kinds = new List<ProjectChangeKind>();
            var historyEvents = 0;
            session.ProjectChanged += (_, e) => kinds.Add(e.Kind);
            session.HistoryChanged += (_, _) => historyEvents++;

            var original = session.Project;
            var before = session.Project.ToJson();
            session.MoveItem(screen.Id, Ms(1_000));
            var after = session.Project.ToJson();

            Assert.True(session.CanUndo);
            Assert.False(session.CanRedo);

            session.Undo();
            Assert.NotSame(original, session.Project);
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.True(session.CanRedo);

            session.Redo();
            Assert.Equal(after, session.Project.ToJson());
            Assert.True(session.CanUndo);
            Assert.False(session.CanRedo);

            Assert.Equal(new[] { ProjectChangeKind.Mapping, ProjectChangeKind.Structural, ProjectChangeKind.Structural }, kinds);
            Assert.Equal(3, historyEvents); // push, undo, redo
            Assert.Equal(3, persist.Writes); // every committed state is persisted, restores included
        }

        [Fact]
        public void A_new_edit_clears_the_redo_stack()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            session.MoveItem(screen.Id, Ms(1_000));
            session.Undo();
            Assert.True(session.CanRedo);

            session.MoveItem(screen.Id, Ms(2_000));
            Assert.False(session.CanRedo);
        }

        [Fact]
        public void The_undo_stack_caps_at_100_entries_evicting_the_oldest()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            for (var i = 0; i < 105; i++)
                session.MoveItem(screen.Id, Ms(10));

            var undos = 0;
            while (session.CanUndo)
            {
                session.Undo();
                undos++;
            }

            Assert.Equal(EditorSession.UndoCapacity, undos);
            // the five evicted entries' moves are unreachable: undo bottoms out at 5 x 10ms.
            Assert.Equal(Ms(50), Resolve(session, screen.Id).TimelineStartTicks);
        }

        // -------------------------------------------------------------------------------- no-ops

        [Fact]
        public void A_no_op_mutation_records_and_notifies_nothing()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var projectEvents = 0;
            var historyEvents = 0;
            session.ProjectChanged += (_, _) => projectEvents++;
            session.HistoryChanged += (_, _) => historyEvents++;

            Assert.Equal(0, session.MoveItem(screen.Id, 0));
            session.SetTrackHidden(screen.TrackId, false); // already visible

            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
            Assert.Equal(0, projectEvents);
            Assert.Equal(0, historyEvents);
        }

        // -------------------------------------------------------------------------------- kinds

        [Fact]
        public void Change_kinds_and_origin_flow_through_to_the_event()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            var origin = new object();
            var seen = new List<(ProjectChangeKind Kind, object Origin)>();
            session.ProjectChanged += (_, e) => seen.Add((e.Kind, e.Origin));

            session.MoveItem(screen.Id, Ms(1_000), origin);
            session.RenameTrack(webcam.TrackId, "Camera");
            session.SetTrackHidden(webcam.TrackId, true);
            session.DeleteItem(webcam.Id, origin);

            Assert.Equal(ProjectChangeKind.Mapping, seen[0].Kind);
            Assert.Same(origin, seen[0].Origin);
            Assert.Equal(ProjectChangeKind.Mapping, seen[1].Kind);
            Assert.Null(seen[1].Origin);
            Assert.Equal(ProjectChangeKind.Structural, seen[2].Kind);
            Assert.Equal(ProjectChangeKind.Structural, seen[3].Kind);
        }

        // ------------------------------------------------------------------------------ gestures

        [Fact]
        public void A_gesture_of_50_moves_is_one_undo_entry_and_previews_never_persist()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var previews = 0;
            var committed = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) =>
            {
                if (e.Kind == ProjectChangeKind.Preview)
                    previews++;
                else
                    committed.Add(e.Kind);
            };

            var before = session.Project.ToJson();
            using (var gesture = session.BeginGesture("Move"))
            {
                for (var i = 0; i < 50; i++)
                    session.MoveItem(screen.Id, Ms(10));

                Assert.Equal(50, previews);
                Assert.Equal(0, persist.Writes);
                Assert.False(session.CanUndo);

                gesture.Commit();
            }

            Assert.Equal(Ms(500), Resolve(session, screen.Id).TimelineStartTicks);
            Assert.Equal(new[] { ProjectChangeKind.Mapping }, committed);
            Assert.Equal(1, persist.Writes);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo); // exactly one entry spanned the whole drag
        }

        [Fact]
        public void Gesture_cancel_and_dispose_without_commit_restore_byte_identical()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var before = session.Project.ToJson();

            var gesture = session.BeginGesture("Move");
            session.MoveItem(screen.Id, Ms(3_000));
            gesture.Cancel();

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);

            // dispose-without-commit is the exception-safety path and must behave identically.
            using (session.BeginGesture("Move"))
                session.MoveItem(screen.Id, Ms(3_000));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void A_no_op_gesture_commit_is_free()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);

            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveItem(screen.Id, 0);
                gesture.Commit();
            }

            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void A_gesture_containing_a_structural_edit_commits_as_structural()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            var committed = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) =>
            {
                if (e.Kind != ProjectChangeKind.Preview)
                    committed.Add(e.Kind);
            };

            using (var gesture = session.BeginGesture("Delete"))
            {
                session.DeleteItem(webcam.Id);
                gesture.Commit();
            }

            Assert.Equal(new[] { ProjectChangeKind.Structural }, committed);
        }

        [Fact]
        public void Gestures_do_not_nest_and_do_not_outlive_their_end()
        {
            var session = NewSession(out _, out _, out _, out _);

            var gesture = session.BeginGesture("Move");
            Assert.Throws<InvalidOperationException>(() => session.BeginGesture("Trim"));
            Assert.Throws<InvalidOperationException>(() => session.Undo());
            gesture.Commit();

            Assert.Throws<InvalidOperationException>(() => gesture.Commit());
            gesture.Dispose(); // disposing an ended gesture is a no-op, not a cancel
        }

        // ---------------------------------------------------------------------------- coalescing

        [Fact]
        public void Same_key_edits_inside_the_window_merge_into_one_undo_entry()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            long now = 0;
            session.Clock = () => now;

            var before = session.Project.ToJson();
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            now += 500;
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x");

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Edits_beyond_the_window_or_with_different_or_null_keys_stay_separate()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            long now = 0;
            session.Clock = () => now;

            // beyond the window
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            now += 1_500;
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x");
            session.Undo();
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
            Assert.True(session.CanUndo);
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);

            // different keys
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            session.EditItem(webcam.Id, i => i.Transform.Y = 0.6, "inspector:y");
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.Y);
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
            session.Undo();

            // null keys never coalesce
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6);
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7);
            session.Undo();
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
        }

        [Fact]
        public void The_window_slides_a_steady_stream_of_edits_into_one_entry()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            long now = 0;
            session.Clock = () => now;

            var before = session.Project.ToJson();
            for (var i = 0; i < 5; i++)
            {
                session.EditItem(webcam.Id, item => item.Transform.X = 0.6 + i * 0.01, "inspector:x");
                now += 900; // each within 1s of the previous, though 4.5s from the first
            }

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Coalescing_never_reaches_across_an_undo_or_redo()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            session.Clock = () => 0;

            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x"); // coalesced
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);
            session.Redo();
            Assert.Equal(0.7, Resolve(session, webcam.Id).Transform.X);

            // an edit right after a redo must not merge into the restored entry.
            session.EditItem(webcam.Id, i => i.Transform.X = 0.8, "inspector:x");
            session.Undo();
            Assert.Equal(0.7, Resolve(session, webcam.Id).Transform.X);
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);
        }

        /// <summary>The belt-and-braces under the inspector's item-scoped keys: even a caller
        /// whose coalesce key names no item cannot merge edits made across a selection change —
        /// the last thing the user did must always have its own undo step.</summary>
        [Fact]
        public void A_selection_change_breaks_coalescing()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            session.Clock = () => 0; // everything lands inside the coalesce window

            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            session.Select(screen.Id);
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x");

            session.Undo();
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
            Assert.True(session.CanUndo);
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);
        }

        // ---------------------------------------------------------------------------- multi-edit

        [Fact]
        public void EditItems_edits_every_item_in_one_mutation_with_one_event_and_one_undo_entry()
        {
            var session = NewSession(out var screen, out var webcam, out _, out var persist);
            var events = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) => events.Add(e.Kind);

            var before = session.Project.ToJson();
            session.EditItems(new[] { screen.Id, webcam.Id }, i => i.Transform.X = 0.8);

            Assert.Equal(0.8, Resolve(session, screen.Id).Transform.X);
            Assert.Equal(0.8, Resolve(session, webcam.Id).Transform.X);
            Assert.Equal(new[] { ProjectChangeKind.Mapping }, events);
            Assert.Equal(1, persist.Writes);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo); // exactly one entry spanned both items

            // an empty id list is a free no-op (the undo itself persisted the restored state)
            var writesAfterUndo = persist.Writes;
            session.EditItems(Array.Empty<Guid>(), i => i.Transform.X = 0.9);
            Assert.False(session.CanUndo);
            Assert.Equal(writesAfterUndo, persist.Writes);
        }

        // ----------------------------------------------------------------------------- selection

        [Fact]
        public void Selection_is_session_owned_and_resolved_live()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var selectionEvents = 0;
            session.SelectionChanged += (_, _) => selectionEvents++;

            session.Select(screen.Id);
            Assert.Equal(new[] { screen.Id }, session.SelectedItemIds);
            Assert.Same(screen, session.PrimarySelectedItem);
            Assert.Equal(1, selectionEvents);

            session.Select(screen.Id); // reselecting is a no-op
            Assert.Equal(1, selectionEvents);

            // undo replaces the item instances; the selection resolves against the new one.
            session.MoveItem(screen.Id, Ms(1_000));
            session.Undo();
            Assert.NotSame(screen, session.PrimarySelectedItem);
            Assert.Equal(screen.Id, session.PrimarySelectedItem.Id);

            session.ClearSelection();
            Assert.Empty(session.SelectedItemIds);
            Assert.Null(session.PrimarySelectedItem);

            session.ClearSelection(); // clearing nothing raises nothing
            Assert.Equal(2, selectionEvents);
        }

        [Fact]
        public void Deleting_the_selected_item_clears_selection_and_undo_restores_it()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            session.Select(screen.Id);

            session.DeleteItem(screen.Id);
            Assert.Empty(session.SelectedItemIds);
            Assert.Null(session.PrimarySelectedItem);

            session.Undo();
            Assert.Equal(new[] { screen.Id }, session.SelectedItemIds);
            Assert.Equal(screen.Id, session.PrimarySelectedItem.Id);
        }

        [Fact]
        public void Selection_restoration_filters_ids_that_no_longer_exist()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            // the session does not validate selected ids up front; the first committed mutation
            // filters them, and the undo entry's dead id filters again on restore.
            session.Select(Guid.NewGuid());
            Assert.Null(session.PrimarySelectedItem);

            session.MoveItem(screen.Id, Ms(1_000));
            Assert.Empty(session.SelectedItemIds);

            session.Undo();
            Assert.Empty(session.SelectedItemIds);
            Assert.Null(session.PrimarySelectedItem);
        }

        // ------------------------------------------------------------------- validation rollback

        [Fact]
        public void A_mutation_that_fails_validation_rolls_back_and_surfaces_without_throwing()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            Assert.True(session.SplitAt(screen.Id, Ms(4_000)));
            var right = session.Project.Items.Single(i => i.TrackId == screen.TrackId && i.Id != screen.Id);

            var before = session.Project.ToJson();
            var writesBefore = persist.Writes;
            var projectEvents = 0;
            session.ProjectChanged += (_, _) => projectEvents++;
            var failures = new List<ValidationFailureEventArgs>();
            session.ValidationFailed += (_, e) => failures.Add(e);

            // sliding the right half back onto the left overlaps them — TimelineOps would never
            // do this, but EditItem accepts arbitrary writes, and validation is the net.
            session.EditItem(right.Id, i => i.TimelineStartTicks = 0);

            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(0, projectEvents);
            Assert.Equal(writesBefore, persist.Writes);
            var failure = Assert.Single(failures);
            Assert.Equal("Edit", failure.Operation);
            Assert.Contains(failure.Errors, e => e.Contains("overlap"));

            // no undo entry was pushed for the failure: one undo unwinds the split, nothing more.
            session.Undo();
            Assert.False(session.CanUndo);
            Assert.Equal(3, session.Project.Items.Count);
        }

        // --------------------------------------------------------------------------- track prune

        [Fact]
        public void Deleting_the_last_item_of_a_session_created_track_removes_the_track_and_undo_restores_both()
        {
            var session = NewSession(out _, out _, out _, out _);

            // both video rows are occupied over [0, 5s), so the image gets a fresh track.
            var image = session.AddImage(@"C:\media\logo.png", 0, Ms(5_000));
            Assert.Equal(4, session.Project.Tracks.Count);
            var afterAdd = session.Project.ToJson();

            session.DeleteItem(image.Id);
            Assert.Equal(3, session.Project.Tracks.Count);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == image.TrackId);

            session.Undo();
            Assert.Equal(afterAdd, session.Project.ToJson()); // track and item come back together
        }

        [Fact]
        public void Initial_tracks_are_never_pruned()
        {
            var session = NewSession(out _, out var webcam, out _, out _);

            session.DeleteItem(webcam.Id);

            Assert.DoesNotContain(session.Project.Items, i => i.TrackId == webcam.TrackId);
            Assert.Contains(session.Project.Tracks, t => t.Id == webcam.TrackId);
        }

        // -------------------------------------------------------------------------- add / import

        [Fact]
        public void AddText_defaults_land_on_the_topmost_free_video_track()
        {
            var session = NewSession(out _, out var webcam, out _, out _);

            // [12s, 17s) is free on both video rows; the webcam row is the topmost.
            var text = session.AddText(Ms(12_000), Ms(5_000));

            Assert.Equal(webcam.TrackId, text.TrackId);
            Assert.Equal(Ms(12_000), text.TimelineStartTicks);
            Assert.Equal(Ms(5_000), text.DurationTicks);

            var content = Assert.IsType<TextContent>(text.Content);
            Assert.Equal("Title", content.Text);
            Assert.Equal(1080 * 0.08, content.Size);
            Assert.Equal("#FFFFFFFF", content.Color);
            Assert.Equal(TextAlign.Center, content.Align);
            Assert.Equal(0.5, text.Transform.X);
            Assert.Equal(0.5, text.Transform.Y);
            Assert.Equal(1.0, text.Transform.Scale);

            // when every video row is busy over the span, a fresh row appears above them all
            // and the audio row shifts down.
            var second = session.AddText(0, Ms(5_000));
            var overlay = session.Project.Tracks.Single(t => t.Id == second.TrackId);
            Assert.Equal("Overlay", overlay.Name);
            Assert.Equal(TrackKind.Video, overlay.Kind);
            Assert.Equal(2, overlay.Order);
            Assert.Equal(3, session.Project.Tracks.Single(t => t.Kind == TrackKind.Audio).Order);
        }

        /// <summary>A hidden row is composed by nothing, so an item added to it is invisible with
        /// no explanation — and the webcam row is Hidden on every fresh edit of a recording whose
        /// overlay was never enabled, making it the topmost "free" row exactly when the timeline
        /// is empty or the playhead sits past the end.</summary>
        [Fact]
        public void AddText_never_lands_on_a_hidden_or_locked_row()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            session.SetTrackHidden(webcam.TrackId, true);

            // the hidden webcam row is skipped even though it is topmost and free
            var text = session.AddText(Ms(12_000), Ms(5_000));
            Assert.Equal(screen.TrackId, text.TrackId);

            // with the only other free row locked, a fresh visible Overlay row is minted instead
            session.SetTrackLocked(screen.TrackId, true);
            var second = session.AddText(Ms(20_000), Ms(5_000));
            var track = session.Project.Tracks.Single(t => t.Id == second.TrackId);
            Assert.Equal("Overlay", track.Name);
            Assert.False(track.Hidden);
            Assert.False(track.Locked);
        }

        [Fact]
        public void AddImage_defaults_to_half_width_centred()
        {
            var session = NewSession(out _, out var webcam, out _, out _);

            var image = session.AddImage(@"C:\media\logo.png", Ms(12_000), Ms(5_000));

            Assert.Equal(webcam.TrackId, image.TrackId);
            Assert.Equal(@"C:\media\logo.png", Assert.IsType<ImageContent>(image.Content).Path);
            Assert.Equal(0.5, image.Transform.X);
            Assert.Equal(0.5, image.Transform.Y);
            Assert.Equal(0.5, image.Transform.Scale);
            Assert.Null(image.LinkGroupId);
        }

        [Fact]
        public void ImportMedia_creates_source_tracks_and_linked_items_in_one_undo_entry()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var recordingGroup = screen.LinkGroupId;
            var before = session.Project.ToJson();

            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), Ms(2_000));

            Assert.Equal(2, created.Count);
            Assert.Empty(session.Project.Validate());

            // the source is mapped exactly as a recording's streams are.
            var source = session.Project.Sources.Single(s => s.Path == @"C:\media\clip.mp4");
            Assert.Equal(new[] { 0, 1 }, source.Streams.Select(s => s.Index).ToArray());
            Assert.Equal(1280, source.Streams[0].Width);
            Assert.Equal(StreamKind.Audio, source.Streams[1].Kind);

            // video row above the recording's video rows, audio row at the very bottom.
            var videoItem = created.Single(i => i.Transform.Scale == 0.5);
            var audioItem = created.Single(i => i != videoItem);
            var videoTrack = session.Project.Tracks.Single(t => t.Id == videoItem.TrackId);
            var audioTrack = session.Project.Tracks.Single(t => t.Id == audioItem.TrackId);
            Assert.Equal(TrackKind.Video, videoTrack.Kind);
            Assert.Equal("clip", videoTrack.Name);
            Assert.Equal(2, videoTrack.Order);
            Assert.Equal(TrackKind.Audio, audioTrack.Kind);
            Assert.Equal(4, audioTrack.Order); // recording audio was bumped to 3

            // one item per stream at the playhead, linked as one file, in-points at zero.
            Assert.All(created, i => Assert.Equal(Ms(2_000), i.TimelineStartTicks));
            Assert.All(created, i => Assert.Equal(0L, ((MediaContent)i.Content).SourceInTicks));
            Assert.Equal(Ms(8_000), videoItem.DurationTicks);
            Assert.Equal(Ms(7_900), audioItem.DurationTicks);
            Assert.NotNull(videoItem.LinkGroupId);
            Assert.Equal(videoItem.LinkGroupId, audioItem.LinkGroupId);
            Assert.NotEqual(recordingGroup, videoItem.LinkGroupId);
            Assert.Equal(0.5, videoItem.Transform.X);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo); // the whole import was one entry
        }

        [Fact]
        public void A_single_stream_import_is_not_linked_so_it_stays_movable()
        {
            var session = NewSession(out _, out _, out _, out _);

            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(withAudio: false), 0);

            var item = Assert.Single(created);
            Assert.Null(item.LinkGroupId);
        }

        /// <summary>The delete-routing discriminator: a recording segment's group has members on
        /// the tracks the session opened with; an import's group lives entirely on session-created
        /// rows. Ripple (and the move gate) applies to the former only.</summary>
        [Fact]
        public void IsRippleGroup_is_true_for_recording_segments_and_false_for_imports()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), Ms(2_000));
            var text = session.AddText(Ms(15_000), Ms(5_000));

            Assert.True(session.IsRippleGroup(screen.Id));
            Assert.False(session.IsRippleGroup(created[0].Id));
            Assert.False(session.IsRippleGroup(text.Id)); // unlinked: a group of one never ripples
            Assert.False(session.IsRippleGroup(Guid.NewGuid()));
        }

        /// <summary>Deleting an imported file must not ripple: an import's link group means
        /// "streams of one file", and closing its gap under everything else silently moved (or,
        /// via the overlap rollback, silently refused to move) unrelated material.</summary>
        [Fact]
        public void DeleteGroup_lifts_the_whole_group_in_place_without_rippling()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), Ms(2_000));
            var text = session.AddText(Ms(15_000), Ms(5_000)); // starts after the import's span
            var before = session.Project.ToJson();

            session.DeleteGroup(created[0].Id);

            // both of the file's items are gone, and nothing else moved — a ripple would have
            // pulled the text card left by the import's 8s span.
            Assert.DoesNotContain(session.Project.Items, i => created.Any(c => c.Id == i.Id));
            Assert.Equal(Ms(15_000), Resolve(session, text.Id).TimelineStartTicks);
            Assert.Equal(0, Resolve(session, screen.Id).TimelineStartTicks);
            // the audio row the import brought is empty and pruned; its video row still holds the
            // text card and survives.
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == created[1].TrackId);
            Assert.Contains(session.Project.Tracks, t => t.Id == created[0].TrackId);
            Assert.Empty(session.Project.Validate());

            session.Undo(); // the whole group delete was one entry
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void RelinkSource_updates_path_and_streams_and_notes_mismatches()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var sourceId = ((MediaContent)screen.Content).SourceId;

            // a clean reprobe: path and stream data replaced, nothing to note.
            var notes = session.RelinkSource(sourceId, @"C:\rec\moved.mp4", RecordingReprobe(@"C:\rec\moved.mp4"));
            Assert.Empty(notes);
            Assert.Equal(@"C:\rec\moved.mp4", session.Project.Sources[0].Path);
            Assert.Empty(session.Project.Validate());

            // a file missing the webcam stream: the old description is kept so the webcam items
            // stay resolvable, and the mismatch is reported.
            notes = session.RelinkSource(sourceId, @"C:\rec\other.mp4",
                RecordingReprobe(@"C:\rec\other.mp4", includeWebcamStream: false));
            var note = Assert.Single(notes);
            Assert.Contains("index 1", note);
            Assert.Contains(session.Project.Sources[0].Streams, s => s.Index == 1 && s.Kind == StreamKind.Video);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>The missing-media dialog's Skip/relink pair: skipping a source disables every
        /// row that plays it in ONE undo entry, and the restore is symmetric — so a Locate in a
        /// later session can bring back exactly what the skip hid.</summary>
        [Fact]
        public void SetSourceRowsEnabled_toggles_every_row_of_the_source_in_one_undo_entry()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var sourceId = ((MediaContent)screen.Content).SourceId;

            session.SetSourceRowsEnabled(sourceId, false);

            Assert.All(session.Project.Tracks.Where(t => t.Kind == TrackKind.Video), t => Assert.True(t.Hidden));
            Assert.All(session.Project.Tracks.Where(t => t.Kind == TrackKind.Audio), t => Assert.True(t.Muted));

            // one undo covers all three rows
            session.Undo();
            Assert.All(session.Project.Tracks, t => Assert.False(t.Hidden || t.Muted));
            session.Redo();

            // the relink flow's other half: pointing the source at its new home and re-enabling
            // leaves no row hidden or muted behind the "found" file.
            session.RelinkSource(sourceId, @"C:\rec\moved.mp4", RecordingReprobe(@"C:\rec\moved.mp4"));
            session.SetSourceRowsEnabled(sourceId, true);
            Assert.All(session.Project.Tracks, t => Assert.False(t.Hidden || t.Muted));

            // already-enabled rows make the restore a free no-op (no undo entry pushed)
            var json = session.Project.ToJson();
            session.SetSourceRowsEnabled(sourceId, true);
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void GetMissingSources_reports_files_that_do_not_exist()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            var missing = Assert.Single(session.GetMissingSources());
            Assert.Equal(((MediaContent)screen.Content).SourceId, missing.Id);
        }

        /// <summary>A source no item plays is never opened — by the player or by the render — so
        /// its file being gone is nothing to prompt (or refuse) over.</summary>
        [Fact]
        public void GetMissingSources_ignores_a_source_nothing_plays()
        {
            var session = NewSession(out _, out _, out _, out _);
            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), 0);

            Assert.Contains(session.GetMissingSources(), s => s.Path == @"C:\media\clip.mp4");
            var clipId = ((MediaContent)created[0].Content).SourceId;
            Assert.True(EditorSession.IsSourceReferenced(session.Project, clipId));

            foreach (var item in created)
                session.DeleteItem(item.Id);

            // the source outlives its items (nothing prunes it); it just stops mattering.
            Assert.Contains(session.Project.Sources, s => s.Path == @"C:\media\clip.mp4");
            Assert.DoesNotContain(session.GetMissingSources(), s => s.Path == @"C:\media\clip.mp4");
            Assert.False(EditorSession.IsSourceReferenced(session.Project, clipId));
        }

        [Fact]
        public void RemoveSource_drops_its_items_and_the_rows_they_emptied_in_one_undo_entry()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), Ms(2_000));
            var clipId = ((MediaContent)created[0].Content).SourceId;
            var before = session.Project.ToJson();

            Assert.True(session.RemoveSource(clipId));

            // the import's source, both its items and both the rows it brought with it are gone…
            Assert.DoesNotContain(session.Project.Sources, s => s.Id == clipId);
            Assert.Equal(3, session.Project.Items.Count);
            Assert.Equal(3, session.Project.Tracks.Count);
            Assert.DoesNotContain(session.Project.Tracks, t => created.Any(i => i.TrackId == t.Id));
            // …and the recording it was imported over is untouched.
            Assert.Contains(session.Project.Items, i => i.Id == screen.Id);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>The missing-media dialog's Remove on the recording itself: rows the session
        /// opened with are normally never pruned, but there is nothing left for them to hold.</summary>
        [Fact]
        public void RemoveSource_removes_the_recordings_own_rows_too()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            Assert.True(session.RemoveSource(((MediaContent)screen.Content).SourceId));

            Assert.Empty(session.Project.Sources);
            Assert.Empty(session.Project.Items);
            Assert.Empty(session.Project.Tracks);
            Assert.Empty(session.Project.Validate());
            Assert.Equal(0, session.DurationTicks);
        }

        [Fact]
        public void RemoveSource_leaves_rows_that_still_hold_something()
        {
            var session = NewSession(out _, out var webcam, out _, out _);

            // a text card shares the webcam row; removing the recording must not take it with it.
            var text = session.AddText(Ms(12_000), Ms(5_000));
            Assert.Equal(webcam.TrackId, text.TrackId);

            Assert.True(session.RemoveSource(session.Project.Sources[0].Id));

            var item = Assert.Single(session.Project.Items);
            Assert.Equal(text.Id, item.Id);
            Assert.Single(session.Project.Tracks);
            Assert.Equal(webcam.TrackId, session.Project.Tracks[0].Id);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void RemoveSource_of_an_unknown_source_changes_nothing()
        {
            var session = NewSession(out _, out _, out _, out var persist);
            var before = session.Project.ToJson();

            Assert.False(session.RemoveSource(Guid.NewGuid()));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void GetTracksForSource_returns_the_rows_the_skip_flow_hides()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);

            var tracks = session.GetTracksForSource(((MediaContent)screen.Content).SourceId);

            Assert.Equal(3, tracks.Count);
            Assert.Contains(tracks, t => t.Id == screen.TrackId);
            Assert.Contains(tracks, t => t.Id == webcam.TrackId);
            Assert.Contains(tracks, t => t.Id == audio.TrackId);
        }

        // ----------------------------------------------------------------------------- snapshots

        [Fact]
        public void SnapshotForPlayer_is_fully_detached_from_the_session()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            var snapshot = session.SnapshotForPlayer();
            Assert.NotSame(session.Project, snapshot);
            Assert.Equal(session.Project.ToJson(), snapshot.ToJson());

            // mutating the snapshot leaves the session alone…
            snapshot.Items.Clear();
            snapshot.Tracks.Clear();
            Assert.Equal(3, session.Project.Items.Count);
            Assert.Equal(3, session.Project.Tracks.Count);

            // …and mutating the session leaves the snapshot alone.
            session.MoveItem(screen.Id, Ms(1_000));
            Assert.Empty(snapshot.Items);
        }

        // ------------------------------------------------------------------------- persistence

        [Fact]
        public void FlushSave_writes_the_latest_pending_state_exactly_once()
        {
            var persist = new CountingPersistence();
            var deferred = new List<Action>();
            var session = new EditorSession(RecordingProject(out var screen, out _, out _),
                persist, save => deferred.Add(save));

            session.MoveItem(screen.Id, Ms(1_000));
            session.MoveItem(screen.Id, Ms(1_000));
            Assert.Equal(0, persist.Writes);

            session.FlushSave();
            Assert.Equal(1, persist.Writes);
            Assert.Equal(session.Project.ToJson(), persist.LastJson);

            // the debounced callbacks find nothing left to write.
            foreach (var save in deferred)
                save();
            Assert.Equal(1, persist.Writes);
        }

        [Fact]
        public void A_session_without_persistence_is_legal()
        {
            var session = new EditorSession(RecordingProject(out var screen, out _, out _), null, null);

            session.MoveItem(screen.Id, Ms(1_000));
            session.FlushSave();
            session.Undo();

            Assert.Equal(0, Resolve(session, screen.Id).TimelineStartTicks);
        }

        // --------------------------------------------------------------------------- empty project

        [Fact]
        public void An_empty_project_is_legal_and_edits_on_it_are_undoable()
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            var session = new EditorSession(project, null, null);
            Assert.Equal(0, session.DurationTicks);

            var before = session.Project.ToJson();
            var text = session.AddText(0, Ms(5_000));

            Assert.NotNull(text);
            var track = Assert.Single(session.Project.Tracks);
            Assert.Equal(TrackKind.Video, track.Kind);
            Assert.Equal(0, track.Order);
            Assert.Equal(Ms(5_000), session.DurationTicks);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.Empty(session.Project.Items);
            Assert.Empty(session.Project.Tracks);
        }
    }
}
