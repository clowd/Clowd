using System;
using System.IO;
using System.Linq;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The CROP section's Manual/Window selector: when it is offered at all, what picking a window
    /// writes (and, just as importantly, what it leaves alone), and how the two modes hand the
    /// panel back and forth. Framework-free like <see cref="InputOverlayInspectorTests"/> — no
    /// Avalonia here.
    ///
    /// The picker reads a real sidecar off disk rather than a stub, because the whole feature hangs
    /// off <see cref="Clowd.VideoSDK.Composition.WindowCapture.Get"/>'s process-wide cache: every
    /// fixture writes a GUID-named temp file and deletes it in a finally, so no two tests can share
    /// a cache entry and a leftover file cannot make a later run pass for the wrong reason.
    /// </summary>
    public class WindowCropInspectorTests
    {
        private const string Header =
            """{"type":"header","version":1,"region":[-100,50,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows"}""";

        private static string Info(int id, string title, string app, int pid = 4212)
            => $$"""{"type":"window_info","id":{{id}},"title":"{{title}}","app":"{{app}}","pid":{{pid}}}""";

        private static string Row(double t, int id, int x, int y, int w, int h, int z = 0)
            => $$"""{"type":"window","t":{{t}},"id":{{id}},"x":{{x}},"y":{{y}},"w":{{w}},"h":{{h}},"z":{{z}}}""";

        /// <summary>The everyday sidecar: an editor window met first, a browser second, both
        /// nameable and neither ours. Ids 7 and 9 are deliberately not 1 and 2 — a bug that
        /// confused a window id with a list index would still pass against 1 and 2.</summary>
        private static string[] TwoWindows() => new[]
        {
            Header,
            Info(7, "README.md", "Code.exe"),
            Info(9, "Inbox", "chrome.exe", 5150),
            Row(0, 7, 100, 100, 800, 600),
            Row(100, 9, 400, 200, 900, 700),
        };

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>Everything one test needs from the fixture. The two screen segments are the
        /// row-fan-out case; the webcam item is the second video stream of the same source, which
        /// is what <c>IsScreenStream</c> keeps the picker away from.</summary>
        private sealed record Fixture(EditorSession Session, SelectedItemViewModel Vm,
            Item Screen, Item Second, Item Webcam);

        /// <summary>A recording cut into two keep-segments over a source carrying a window sidecar
        /// (or none, for <paramref name="windowCapturePath"/> null), plus a webcam row off the same
        /// source. Shaped like <see cref="InputOverlayInspectorTests"/>'s fixture: each screen
        /// segment is its own link group, so a row-wide write has two items to reach.</summary>
        private static Fixture NewInspector(string windowCapturePath)
        {
            var sourceId = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        InputCapturePath = @"C:\rec\input-capture.jsonl",
                        WindowCapturePath = windowCapturePath,
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { screenTrack, webcamTrack, audioTrack },
            };

            var screen = AddSegment(project, screenTrack, audioTrack, sourceId, 0, Ms(8_000));
            var second = AddSegment(project, screenTrack, audioTrack, sourceId, Ms(8_000), Ms(12_000));
            var webcam = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = webcamTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(20_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
            };
            project.Items.Add(webcam);

            var session = new EditorSession(project, null, null);
            return new Fixture(session, new SelectedItemViewModel { Session = session }, screen, second, webcam);
        }

        private static Item AddSegment(Project project, Track screen, Track audio, Guid sourceId,
            long startTicks, long durationTicks)
        {
            var group = Guid.NewGuid();
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screen.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = startTicks },
                LinkGroupId = group,
            };
            project.Items.Add(item);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audio.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 2, SourceInTicks = startTicks },
                LinkGroupId = group,
            });
            return item;
        }

        /// <summary>Writes a sidecar to a GUID-named temp file, runs the body against a fixture
        /// pointed at it, and deletes the file whatever happens.</summary>
        private static void WithWindowCapture(string[] lines, Action<Fixture> body)
        {
            var path = Path.Combine(Path.GetTempPath(),
                "clowd-window-capture-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(path, String.Join("\n", lines));
            try
            {
                body(NewInspector(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Live item by id — undo replaces the project instance, so a test must
        /// re-resolve exactly as the view model does.</summary>
        private static Item Live(EditorSession session, Guid id) =>
            session.Project.Items.First(i => i.Id == id);

        private static WindowCrop FollowOf(EditorSession session, Guid id) =>
            Live(session, id).Transform?.CropWindow;

        // ------------------------------------------------------------------------ when it is offered

        [Fact]
        public void The_crop_source_selector_is_hidden_without_a_sidecar()
        {
            var fx = NewInspector(null);
            fx.Session.Select(fx.Screen.Id);

            // the CROP section itself is exactly what it is today: four spinners, no tiles
            Assert.True(fx.Vm.ShowCrop);
            Assert.False(fx.Vm.ShowCropSource);
            Assert.True(fx.Vm.CropManual);
            Assert.True(fx.Vm.ShowCropInsets);
            Assert.Empty(fx.Vm.CropWindowOptions);
        }

        [Fact]
        public void The_crop_source_selector_is_hidden_on_a_webcam_row()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Webcam.Id);

                // the geometry is measured against the screen capture region and means nothing here
                Assert.True(fx.Vm.ShowCrop);
                Assert.False(fx.Vm.ShowCropSource);
                Assert.Empty(fx.Vm.CropWindowOptions);

                // ...and the same sidecar does light the selector on the screen row, so this is the
                // stream test and not a broken fixture
                fx.Session.Select(fx.Screen.Id);
                Assert.True(fx.Vm.ShowCropSource);
            });
        }

        [Fact]
        public void The_crop_source_selector_is_hidden_when_the_sidecar_names_no_windows()
        {
            WithWindowCapture(new[] { Header }, fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                Assert.False(fx.Vm.ShowCropSource);
                Assert.Empty(fx.Vm.CropWindowOptions);
            });
        }

        // ------------------------------------------------------------------------------- the write

        [Fact]
        public void Picking_window_mode_writes_the_follow_and_nothing_else()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                fx.Vm.CropLeft = 0.1;
                fx.Vm.Aspect169 = true;

                var before = Live(fx.Session, fx.Screen.Id).Transform;
                var aspect = before.Aspect;
                var scaleY = before.ScaleY;
                var left = before.Crop.Left;
                Assert.NotNull(aspect); // else the equality below would pass on two nulls

                fx.Vm.CropFollowsWindow = true;

                // the mode picks the first offered window in the same call, so the project never
                // holds a follow with nothing followed
                var after = Live(fx.Session, fx.Screen.Id).Transform;
                Assert.Equal(7, after.CropWindow.WindowId);
                Assert.Equal("README.md", after.CropWindow.Title);
                Assert.Equal("Code.exe", after.CropWindow.App);
                Assert.Equal(4212, after.CropWindow.Pid);

                // the resolved crop carries the item's own box ratio, so there is no height or
                // ratio to seed and the hand-cut insets stay untouched underneath
                Assert.Equal(aspect, after.Aspect);
                Assert.Equal(scaleY, after.ScaleY);
                Assert.Equal(left, after.Crop.Left);
            });
        }

        [Fact]
        public void Picking_window_mode_with_no_windows_is_refused()
        {
            WithWindowCapture(new[] { Header }, fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                fx.Vm.CropFollowsWindow = true;

                Assert.True(fx.Vm.CropManual);
                Assert.False(fx.Vm.CropFollowsWindow);
                Assert.Null(FollowOf(fx.Session, fx.Screen.Id));
                Assert.False(fx.Session.CanUndo);
            });
        }

        [Fact]
        public void A_window_pick_writes_the_whole_linked_row()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                fx.Vm.CropWindow = fx.Vm.CropWindowOptions.First(o => o.Id == 9);

                // where a picture is framed is a property of the feed, not of a segment
                Assert.Equal(9, FollowOf(fx.Session, fx.Screen.Id).WindowId);
                Assert.Equal(9, FollowOf(fx.Session, fx.Second.Id).WindowId);
                Assert.Equal("Inbox", FollowOf(fx.Session, fx.Second.Id).Title);

                // ...and the selection moving along the row reads the same pick back
                fx.Session.Select(fx.Second.Id);
                Assert.True(fx.Vm.CropFollowsWindow);
                Assert.Equal(9, fx.Vm.CropWindow.Id);
            });
        }

        // -------------------------------------------------------------------------------- the modes

        [Fact]
        public void Switching_to_window_mode_is_one_undo_entry()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                Assert.False(fx.Session.CanUndo);

                fx.Vm.CropFollowsWindow = true;
                Assert.True(fx.Vm.CropFollowsWindow);

                // entering the mode and choosing the window are one write, so one undo puts the
                // panel back to Manual with nothing left behind
                fx.Session.Undo();

                Assert.True(fx.Vm.CropManual);
                Assert.Null(FollowOf(fx.Session, fx.Screen.Id));
                Assert.False(fx.Session.CanUndo);
            });
        }

        /// <summary>The follow's coalesce key is its own: typing an inset and then switching mode
        /// happen well inside the coalesce window, and merging them would make one undo undo
        /// both.</summary>
        [Fact]
        public void The_follow_does_not_coalesce_with_an_inset_edit()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                fx.Vm.CropLeft = 0.2;
                fx.Vm.CropFollowsWindow = true;

                fx.Session.Undo();

                Assert.True(fx.Vm.CropManual);
                Assert.Equal(0.2, Live(fx.Session, fx.Screen.Id).Transform.Crop.Left);
                Assert.True(fx.Session.CanUndo);
            });
        }

        [Fact]
        public void Leaving_window_mode_keeps_the_stored_insets()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                fx.Vm.CropLeft = 0.2;

                // the insets go on being offered and applied in window mode, only measured
                // against the window instead of the picture
                fx.Vm.CropFollowsWindow = true;
                Assert.True(fx.Vm.ShowCropInsets);
                Assert.Equal(0.2, fx.Vm.CropLeft);

                fx.Vm.CropManual = true;

                // the follow never wrote to Transform.Crop, so the spinners still hold exactly
                // the insets the user cut
                Assert.Equal(0.2, fx.Vm.CropLeft);
                Assert.Equal(0.2, Live(fx.Session, fx.Screen.Id).Transform.Crop.Left);
                Assert.Null(FollowOf(fx.Session, fx.Screen.Id));
                Assert.True(fx.Vm.ShowCropInsets);
            });
        }

        [Fact]
        public void The_inset_rows_stay_offered_in_window_mode()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                Assert.True(fx.Vm.ShowCropInsets);
                Assert.False(fx.Vm.ShowCropWindow);

                // following a window does not take the crop away, it changes what the insets are
                // measured against: WindowCropMath.CropFor reads them as fractions of the window,
                // which is how a title bar is cut off and stays off as the window moves
                fx.Vm.CropFollowsWindow = true;
                Assert.True(fx.Vm.ShowCropWindow);
                Assert.True(fx.Vm.ShowCropInsets);

                fx.Vm.CropTop = 0.08;
                Assert.Equal(0.08, Live(fx.Session, fx.Screen.Id).Transform.Crop.Top);
                Assert.Equal(7, FollowOf(fx.Session, fx.Screen.Id).WindowId);
            });
        }

        [Fact]
        public void The_aspect_section_is_hidden_in_window_mode()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                fx.Vm.Aspect169 = true;
                Assert.True(fx.Vm.ShowAspect);
                Assert.True(fx.Vm.AspectFitEnabled);

                // the window's rect is the shown region and the box takes its shape, so a preset,
                // a fit mode and a free height are all dropped by WindowCropMath.Effective: the
                // block is hidden rather than left writing state nothing draws
                fx.Vm.CropFollowsWindow = true;
                Assert.False(fx.Vm.ShowAspect);
                // and no tile reads as applied — Unlocked would put free-resize edge handles on
                // the gizmo for an axis the picture no longer takes from the transform
                Assert.True(fx.Vm.AspectOriginal);
                Assert.False(fx.Vm.AspectUnlocked);

                // the stored ratio was never cleared, so the way back restores the item
                fx.Vm.CropManual = true;
                Assert.True(fx.Vm.ShowAspect);
                Assert.True(fx.Vm.Aspect169);
                Assert.True(fx.Vm.AspectFitEnabled);
            });
        }

        [Fact]
        public void Window_mode_ends_crop_mode_active()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                fx.Vm.CropModeActive = true;
                Assert.True(fx.Vm.CropModeActive);

                // the gizmo's crop drag is a conversation about the four insets, which this mode
                // has none of
                fx.Vm.CropFollowsWindow = true;

                Assert.False(fx.Vm.CropModeActive);
            });
        }

        // ------------------------------------------------------------------------------ the picker

        [Fact]
        public void A_stored_window_the_sidecar_does_not_name_still_reads_as_picked()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                // a project copied onto a re-recorded session: the pick's id is not in this file
                fx.Session.EditItem(fx.Screen.Id, i => (i.Transform ??= new Transform()).CropWindow =
                    new WindowCrop { WindowId = 404, Title = "Notes", App = "Old.exe", Pid = 88 });
                fx.Session.Select(fx.Screen.Id);

                Assert.True(fx.Vm.CropFollowsWindow);
                Assert.Equal(404, fx.Vm.CropWindow.Id);
                Assert.EndsWith("(missing)", fx.Vm.CropWindow.Label);
                Assert.StartsWith("Notes", fx.Vm.CropWindow.Label);
                // it is a row of the picker, so the closed button is never blank
                Assert.Equal(3, fx.Vm.CropWindowOptions.Count);
                Assert.Contains(fx.Vm.CropWindowOptions, o => o.Id == 404);
            });
        }

        [Fact]
        public void Clowds_recording_chrome_is_not_offered_but_its_ordinary_windows_are()
        {
            WithWindowCapture(new[]
            {
                Header,
                Info(7, "README.md", "Code.exe"),
                // Clowd's own window is ordinary content: recording it is the point of this
                // branch, and it stays offered even when the editor is the process that recorded
                // it. Only the share-region helper's mirror window is chrome.
                Info(8, "Clowd", "Clowd.Ui.exe", 6001),
                Info(9, "Clowd", "Clowd.Ui.exe", Environment.ProcessId),
                Info(10, "Region", "clowd_share_region.exe", 6002),
                Row(0, 7, 100, 100, 800, 600),
                Row(10, 8, 0, 0, 400, 300),
                Row(20, 9, 900, 0, 400, 300),
                Row(30, 10, 0, 700, 400, 300),
            }, fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                Assert.Equal(new[] { 7, 8, 9 }, fx.Vm.CropWindowOptions.Select(o => o.Id).ToArray());
            });
        }

        [Fact]
        public void A_window_covered_for_the_whole_recording_is_not_offered()
        {
            WithWindowCapture(new[]
            {
                Header,
                Info(7, "README.md", "Code.exe"),
                Info(8, "Inbox", "chrome.exe", 5150),
                // 8 sits under a window that fills the region for every sampled instant, so not
                // one of its pixels reached the video and following it would frame 7's contents
                Row(0, 7, -200, -200, 3000, 2000, 0),
                Row(0, 8, 100, 100, 800, 600, 1),
                Row(500, 8, 140, 100, 800, 600, 1),
            }, fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                Assert.Equal(new[] { 7 }, fx.Vm.CropWindowOptions.Select(o => o.Id).ToArray());
            });
        }

        [Fact]
        public void A_window_uncovered_for_a_single_instant_is_offered()
        {
            WithWindowCapture(new[]
            {
                Header,
                Info(7, "README.md", "Code.exe"),
                Info(8, "Inbox", "chrome.exe", 5150),
                Row(0, 7, -200, -200, 3000, 2000, 0),
                Row(0, 8, 100, 100, 800, 600, 1),
                // the coverer leaves the region for one poll: a sliver of a second on screen is
                // still on screen
                Row(500, 7, 0, 0, 0, 0, 0),
                Row(600, 7, -200, -200, 3000, 2000, 0),
            }, fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                Assert.Equal(new[] { 7, 8 }, fx.Vm.CropWindowOptions.Select(o => o.Id).ToArray());
            });
        }

        [Fact]
        public void A_stored_pick_the_list_filtered_out_still_reads_as_an_ordinary_row()
        {
            WithWindowCapture(new[]
            {
                Header,
                Info(7, "README.md", "Code.exe"),
                Info(8, "Inbox", "chrome.exe", 5150),
                Row(0, 7, -200, -200, 3000, 2000, 0),
                Row(0, 8, 100, 100, 800, 600, 1),
            }, fx =>
            {
                // picked before the window was hidden from the list — or by hand. The crop it
                // drives is real and still applied, so the row must not claim otherwise.
                fx.Session.EditItem(fx.Screen.Id, i => (i.Transform ??= new Transform()).CropWindow =
                    new WindowCrop { WindowId = 8, Title = "Inbox", App = "chrome.exe", Pid = 5150 });
                fx.Session.Select(fx.Screen.Id);

                Assert.Equal(8, fx.Vm.CropWindow.Id);
                Assert.Equal("Inbox", fx.Vm.CropWindow.Label);
                Assert.Equal(new[] { 7, 8 }, fx.Vm.CropWindowOptions.Select(o => o.Id).ToArray());
            });
        }

        [Fact]
        public void Windows_sharing_a_title_are_disambiguated()
        {
            WithWindowCapture(new[]
            {
                Header,
                Info(7, "README.md", "Code.exe"),
                Info(9, "README.md", "chrome.exe", 5150),
                Row(0, 7, 100, 100, 800, 600),
                Row(100, 9, 400, 200, 900, 700),
            }, fx =>
            {
                fx.Session.Select(fx.Screen.Id);

                Assert.Equal(new[] { "README.md (Code.exe)", "README.md (chrome.exe)" },
                    fx.Vm.CropWindowOptions.Select(o => o.Label).ToArray());
            });
        }

        /// <summary>The picker's rows are matched by reference, so rebuilding the list swaps the
        /// control's ItemsSource out from under the pick. Moving along one recording's row must
        /// not do that.</summary>
        [Fact]
        public void The_option_list_is_stable_while_the_recording_is()
        {
            WithWindowCapture(TwoWindows(), fx =>
            {
                fx.Session.Select(fx.Screen.Id);
                var offered = fx.Vm.CropWindowOptions;
                Assert.Equal(2, offered.Count);

                fx.Session.Select(fx.Second.Id);

                Assert.Same(offered, fx.Vm.CropWindowOptions);
            });
        }
    }
}
