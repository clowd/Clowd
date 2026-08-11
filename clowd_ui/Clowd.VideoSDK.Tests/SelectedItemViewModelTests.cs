using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The inspector's view model over <see cref="EditorSession"/>. Everything here runs with no
    /// Avalonia application started (see <see cref="NeedsNoAvaloniaApplication"/>) — the view model
    /// is deliberately framework-free so the session↔bindings mediation is testable directly, and
    /// that property is worth keeping.
    /// </summary>
    public class SelectedItemViewModelTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The recording shape plus the two synthetic item kinds the inspector has
        /// sections for: three linked items (screen / webcam / audio) covering [0, 10s), the webcam
        /// row split into two linked segments, and a text card on an overlay row.</summary>
        private static Project Fixture(out Item screen, out Item webcamA, out Item webcamB,
            out Item audio, out Item text)
        {
            var sourceId = Guid.NewGuid();
            var linkGroup = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            var textTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Overlay", Order = 2 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 3 };

            Item Media(Track track, int streamIndex, long start, long duration) => new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex, SourceInTicks = start },
                LinkGroupId = linkGroup,
            };

            screen = Media(screenTrack, 0, 0, Ms(10_000));
            // the webcam row is split: two linked segments of the same camera, which is exactly the
            // case a row-wide placement edit has to cover.
            webcamA = Media(webcamTrack, 1, 0, Ms(4_000));
            webcamB = Media(webcamTrack, 1, Ms(4_000), Ms(6_000));
            audio = Media(audioTrack, 2, 0, Ms(10_000));
            text = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = textTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = new TextContent { Text = "Title", Size = 86, Color = "#FFFFFFFF", Align = TextAlign.Center },
            };

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
                Tracks = { screenTrack, webcamTrack, textTrack, audioTrack },
                Items = { screen, webcamA, webcamB, audio, text },
            };
        }

        private static (EditorSession Session, SelectedItemViewModel Vm) NewInspector(
            out Item screen, out Item webcamA, out Item webcamB, out Item audio, out Item text)
        {
            var session = new EditorSession(Fixture(out screen, out webcamA, out webcamB, out audio, out text), null, null);
            return (session, new SelectedItemViewModel { Session = session });
        }

        /// <summary>Live item by id — the model instances the fixture handed out are replaced by
        /// undo, so tests must re-resolve exactly as the view model does.</summary>
        private static Item Live(EditorSession session, Guid id) =>
            session.Project.Items.First(i => i.Id == id);

        // ------------------------------------------------------------------ framework-freedom

        [Fact]
        public void NeedsNoAvaloniaApplication()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);
            vm.PositionX = 0.25;

            Assert.Equal(0.25, Live(session, screen.Id).Transform.X);
            Assert.Null(Avalonia.Application.Current);
        }

        // ------------------------------------------------------------------------ section flags

        [Fact]
        public void NoSelection_ShowsNothing()
        {
            var (_, vm) = NewInspector(out _, out _, out _, out _, out _);

            Assert.False(vm.HasSelection);
            Assert.False(vm.ShowTransform);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowTransitions);
        }

        [Fact]
        public void VideoItem_ShowsPictureSections()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            Assert.True(vm.HasSelection);
            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowScale);
            Assert.True(vm.ShowMask);
            Assert.True(vm.ShowCrop);
            Assert.True(vm.ShowTransitions);
            Assert.True(vm.ShowTrackHidden);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowTrackMuted);
            Assert.Equal("Screen", vm.SubjectName);
            Assert.Equal("Video", vm.SubjectKind);
        }

        [Fact]
        public void AudioItem_ShowsVolumeOnly()
        {
            var (session, vm) = NewInspector(out _, out _, out _, out var audio, out _);
            session.Select(audio.Id);

            Assert.True(vm.ShowAudio);
            Assert.True(vm.ShowTransitions);
            Assert.True(vm.ShowTrackMuted);
            Assert.False(vm.ShowTransform);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowTrackHidden);
            Assert.Equal("Audio", vm.SubjectKind);
        }

        [Fact]
        public void TextItem_ShowsTextAndScaleButNoMask()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out var text);
            session.Select(text.Id);

            Assert.True(vm.ShowText);
            Assert.True(vm.ShowTransform);
            // the gizmo's corner drag writes Transform.Scale for text too, so the field must be
            // on show — relabelled, because for text it multiplies the natural block rather than
            // mapping to a canvas fraction and must not collide with the font-size "Size".
            Assert.True(vm.ShowScale);
            Assert.Equal("Text scale", vm.ScaleLabel);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowAudio);
            Assert.Equal("Title", vm.Text);
            Assert.Equal(86, vm.FontSize);
            Assert.Equal(TextAlign.Center, vm.TextAlign);

            session.Select(screen.Id);
            Assert.Equal("Size", vm.ScaleLabel);
        }

        [Fact]
        public void SelectionChange_ReReadsValues()
        {
            var (session, vm) = NewInspector(out var screen, out var webcamA, out _, out _, out _);
            session.EditItem(webcamA.Id, i => i.Transform.X = 0.8, origin: null);

            session.Select(screen.Id);
            Assert.Equal(0.5, vm.PositionX);

            session.Select(webcamA.Id);
            Assert.Equal(0.8, vm.PositionX);

            session.ClearSelection();
            Assert.False(vm.HasSelection);
        }

        // ----------------------------------------------------------------------------- writing

        [Fact]
        public void TextSetters_WriteThroughTheSession()
        {
            var (session, vm) = NewInspector(out _, out _, out _, out _, out var text);
            session.Select(text.Id);

            vm.Text = "Hello";
            vm.FontSize = 60;
            vm.TextAlign = TextAlign.Right;
            vm.TextColorHex = "#FF00FF00";

            var content = (TextContent)Live(session, text.Id).Content;
            Assert.Equal("Hello", content.Text);
            Assert.Equal(60, content.Size);
            Assert.Equal(TextAlign.Right, content.Align);
            Assert.Equal("#FF00FF00", content.Color);
        }

        [Fact]
        public void HalfTypedColor_NeverReachesTheModel()
        {
            var (session, vm) = NewInspector(out _, out _, out _, out _, out var text);
            session.Select(text.Id);

            vm.TextColorHex = "#FF00";

            Assert.Equal("#FFFFFFFF", ((TextContent)Live(session, text.Id).Content).Color);
            Assert.Equal("#FF00", vm.TextColorHex); // still in the box, waiting to be finished
        }

        [Fact]
        public void VolumeIsPerItem_NotPerRow()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out var webcamB, out _, out _);
            session.Select(webcamA.Id);

            vm.Volume = 0.4;

            Assert.Equal(0.4, Live(session, webcamA.Id).Volume);
            Assert.Equal(1.0, Live(session, webcamB.Id).Volume);
        }

        [Fact]
        public void ConsecutiveSpinnerEdits_CoalesceIntoOneUndoEntry()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            vm.PositionX = 0.6;
            vm.PositionX = 0.7;

            Assert.Equal(0.7, Live(session, screen.Id).Transform.X);

            session.Undo();
            Assert.Equal(0.5, Live(session, screen.Id).Transform.X);
            Assert.False(session.CanUndo); // both edits were one entry
            Assert.Equal(0.5, vm.PositionX);
        }

        /// <summary>A selection change must start a new undo entry even inside the coalesce
        /// window: without item-scoped keys (and the session's own break-on-select), spinning
        /// item A's opacity and then item B's within a second would merge both items' edits into
        /// one "sel:opacity" entry — one Ctrl+Z would revert both, and the second edit would have
        /// no undo step of its own.</summary>
        [Fact]
        public void EditsToDifferentItems_NeverCoalesce()
        {
            var (session, vm) = NewInspector(out var screen, out _, out var webcamB, out _, out _);
            session.Clock = () => 0; // everything lands inside the coalesce window
            session.Select(screen.Id);
            vm.Opacity = 0.5;

            session.Select(webcamB.Id);
            vm.Opacity = 0.8;

            session.Undo(); // only the webcam edit
            Assert.Equal(1.0, Live(session, webcamB.Id).Transform.Opacity);
            Assert.Equal(0.5, Live(session, screen.Id).Transform.Opacity);
            Assert.True(session.CanUndo);

            session.Undo();
            Assert.Equal(1.0, Live(session, screen.Id).Transform.Opacity);
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void DifferentProperties_DoNotCoalesceTogether()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            vm.PositionX = 0.6;
            vm.Opacity = 0.5;

            session.Undo();
            Assert.Equal(1.0, Live(session, screen.Id).Transform.Opacity);
            Assert.Equal(0.6, Live(session, screen.Id).Transform.X);
            Assert.True(session.CanUndo);
        }

        [Fact]
        public void NanFromATextBox_CannotPoisonTheProject()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            vm.Scale = Double.NaN;

            Assert.Equal(SelectedItemViewModel.MinScale, Live(session, screen.Id).Transform.Scale);
        }

        // -------------------------------------------------------------------------- row fan-out

        [Fact]
        public void LinkedRowEdit_FansOutToEverySegmentAsOneChange()
        {
            var (session, vm) = NewInspector(out var screen, out var webcamA, out var webcamB, out _, out _);
            session.Select(webcamA.Id);

            var changes = 0;
            session.ProjectChanged += (_, _) => changes++;

            vm.PositionX = 0.82;

            // both segments of the camera row moved — placement belongs to the feed, not the cut …
            Assert.Equal(0.82, Live(session, webcamA.Id).Transform.X);
            Assert.Equal(0.82, Live(session, webcamB.Id).Transform.X);
            // … but not the other rows, …
            Assert.Equal(0.5, Live(session, screen.Id).Transform.X);
            // … and it cost one pipeline run and one undo entry, not one per segment.
            Assert.Equal(1, changes);

            session.Undo();
            Assert.Equal(0.5, Live(session, webcamA.Id).Transform.X);
            Assert.Equal(0.5, Live(session, webcamB.Id).Transform.X);
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void MaskEdit_FansOutButTransitionsStaySingleItem()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out var webcamB, out _, out _);
            session.Select(webcamA.Id);

            vm.MaskCircle = true;
            vm.EntryKind = TransitionKind.Fade;

            Assert.Equal(MaskShape.Circle, Live(session, webcamA.Id).Transform.Mask.Shape);
            Assert.Equal(MaskShape.Circle, Live(session, webcamB.Id).Transform.Mask.Shape);

            Assert.NotNull(Live(session, webcamA.Id).Entry);
            Assert.Null(Live(session, webcamB.Id).Entry);
        }

        [Fact]
        public void UnlinkedItem_EditsOnlyItself()
        {
            var (session, vm) = NewInspector(out _, out _, out _, out _, out var text);
            session.Select(text.Id);

            vm.PositionY = 0.2;

            Assert.Equal(0.2, Live(session, text.Id).Transform.Y);
            Assert.False(vm.IsLinked);
        }

        // -------------------------------------------------------------------------------- mask

        [Fact]
        public void MaskShapeFlips_PreserveTheCornerRadius()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out _, out _, out _);
            session.Select(webcamA.Id);

            vm.MaskRounded = true;
            vm.CornerRadius = 0.4;
            Assert.True(vm.ShowCornerRadius);

            // rounded -> circle: the radius is meaningless for a circle but must survive the trip
            vm.MaskCircle = true;
            Assert.False(vm.ShowCornerRadius);
            Assert.Equal(0.4, Live(session, webcamA.Id).Transform.Mask.CornerRadius);

            vm.MaskRounded = true;
            Assert.Equal(0.4, vm.CornerRadius);
            Assert.Equal(0.4, Live(session, webcamA.Id).Transform.Mask.CornerRadius);

            // rounded -> squircle: also radius-free, and also must not lose the number
            vm.MaskSquircle = true;
            Assert.False(vm.ShowCornerRadius);
            Assert.Equal(MaskShape.Squircle, Live(session, webcamA.Id).Transform.Mask.Shape);
            Assert.Equal(0.4, Live(session, webcamA.Id).Transform.Mask.CornerRadius);

            // masked -> unmasked drops the radius from the model, so the view model remembers it
            vm.MaskSquare = true;
            Assert.Null(Live(session, webcamA.Id).Transform.Mask);

            vm.MaskRounded = true;
            Assert.Equal(0.4, vm.CornerRadius);
            Assert.Equal(0.4, Live(session, webcamA.Id).Transform.Mask.CornerRadius);
        }

        [Fact]
        public void MaskShape_SelectingOneTileDeselectsTheOthers()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out _, out _, out _);
            session.Select(webcamA.Id);

            Assert.True(vm.MaskSquare);

            vm.MaskSquircle = true;
            Assert.False(vm.MaskSquare);
            Assert.False(vm.MaskCircle);
            Assert.False(vm.MaskRounded);
            Assert.Equal(MaskShape.Squircle, Live(session, webcamA.Id).Transform.Mask.Shape);
        }

        [Fact]
        public void MaskRadioDeselection_DoesNotWriteTheModel()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out _, out _, out _);
            session.Select(webcamA.Id);
            vm.MaskCircle = true;

            var before = session.Project.ToJson();
            // what a radio group does to the losing button when another is checked
            vm.MaskCircle = false;

            Assert.Equal(before, session.Project.ToJson());
        }

        // -------------------------------------------------------------------------------- crop

        [Fact]
        public void CropInsets_WriteTheWholeRowAndCollapseBackToNoCrop()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out var webcamB, out _, out _);
            session.Select(webcamA.Id);
            var before = session.Project.ToJson();

            vm.CropLeft = 0.1;
            vm.CropBottom = 0.25;

            // placement belongs to the feed, not the cut: both segments of the row carry it
            foreach (var id in new[] { webcamA.Id, webcamB.Id })
            {
                var crop = Live(session, id).Transform.Crop;
                Assert.Equal(0.1, crop.Left);
                Assert.Equal(0.25, crop.Bottom);
                Assert.Equal(0, crop.Top);
                Assert.Equal(0, crop.Right);
            }

            // back to zero on every side: "no crop" has one representation, so the object goes too
            // and the project is byte-identical to the one that never had a crop.
            vm.CropLeft = 0;
            Assert.NotNull(Live(session, webcamA.Id).Transform.Crop);
            vm.CropBottom = 0;

            Assert.Null(Live(session, webcamA.Id).Transform.Crop);
            Assert.Null(Live(session, webcamB.Id).Transform.Crop);
            Assert.Equal(before, session.Project.ToJson());
        }

        // ------------------------------------------------------------------------- transitions

        [Fact]
        public void TransitionKind_RoundTripsThroughNone()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            Assert.Equal(TransitionKind.None, vm.EntryKind);
            Assert.False(vm.ShowEntryOptions);

            vm.EntryKind = TransitionKind.SlideLeft;

            var entry = Live(session, screen.Id).Entry;
            Assert.NotNull(entry);
            Assert.Equal(TransitionKind.SlideLeft, entry.Kind);
            Assert.Equal(SelectedItemViewModel.DefaultTransitionMs * TimeSpan.TicksPerMillisecond, entry.DurationTicks);
            Assert.Equal(SelectedItemViewModel.DefaultTransitionEasing, entry.Easing);
            Assert.True(vm.ShowEntryOptions);

            vm.EntryDurationMs = 750;
            vm.EntryEasing = TransitionEasing.Linear;
            entry = Live(session, screen.Id).Entry;
            Assert.Equal(750 * TimeSpan.TicksPerMillisecond, entry.DurationTicks);
            Assert.Equal(TransitionEasing.Linear, entry.Easing);

            // None means no transition object at all, not a Kind=None one
            vm.EntryKind = TransitionKind.None;
            Assert.Null(Live(session, screen.Id).Entry);
            Assert.False(vm.ShowEntryOptions);

            // turning it back on keeps the numbers the user was working with
            vm.EntryKind = TransitionKind.Fade;
            entry = Live(session, screen.Id).Entry;
            Assert.Equal(750 * TimeSpan.TicksPerMillisecond, entry.DurationTicks);
            Assert.Equal(TransitionEasing.Linear, entry.Easing);
        }

        [Fact]
        public void ExitTransition_IsIndependentOfEntry()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            vm.ExitKind = TransitionKind.Wipe;

            Assert.Null(Live(session, screen.Id).Entry);
            Assert.Equal(TransitionKind.Wipe, Live(session, screen.Id).Exit.Kind);
            Assert.True(vm.ShowExitOptions);
            Assert.False(vm.ShowEntryOptions);
        }

        // -------------------------------------------------------------- external change / undo

        [Fact]
        public void ChangeFromElsewhere_IsReRead()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            // the timeline, the gizmo, a script — anything that is not this view model
            session.EditItem(screen.Id, i =>
            {
                i.Transform.X = 0.31;
                i.Transform.Opacity = 0.6;
                i.Volume = 0.25;
                i.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(500), Easing = TransitionEasing.CubicIn };
            }, origin: new object());

            Assert.Equal(0.31, vm.PositionX);
            Assert.Equal(0.6, vm.Opacity);
            Assert.Equal(0.25, vm.Volume);
            Assert.Equal(TransitionKind.Fade, vm.EntryKind);
            Assert.Equal(500, vm.EntryDurationMs);
            Assert.Equal(TransitionEasing.CubicIn, vm.EntryEasing);
        }

        [Fact]
        public void TrackToggle_MirrorsTheRowAndReReadsExternalChanges()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);
            var trackId = screen.TrackId;

            vm.TrackHidden = true;
            Assert.True(session.Project.Tracks.First(t => t.Id == trackId).Hidden);

            session.SetTrackHidden(trackId, false, origin: null);
            Assert.False(vm.TrackHidden);
        }

        [Fact]
        public void Unlink_ClearsTheRowsLinkageAndTheInspectorSeesIt()
        {
            var (session, vm) = NewInspector(out _, out var webcamA, out var webcamB, out _, out _);
            session.Select(webcamA.Id);
            Assert.True(vm.IsLinked);

            ((System.Windows.Input.ICommand)vm.CommandUnlink).Execute(null);

            Assert.False(vm.IsLinked);
            Assert.Null(Live(session, webcamA.Id).LinkGroupId);
            Assert.Null(Live(session, webcamB.Id).LinkGroupId);
        }

        [Fact]
        public void DeletingTheSelection_ClearsIt_AndUndoBringsItBack()
        {
            var (session, vm) = NewInspector(out _, out _, out _, out _, out var text);
            session.Select(text.Id);
            vm.PositionX = 0.3;

            session.DeleteItem(text.Id, origin: null);
            Assert.False(vm.HasSelection);
            Assert.False(vm.ShowText);

            session.Undo(); // restores the item AND the selection it was deleted under
            Assert.True(vm.HasSelection);
            Assert.True(vm.ShowText);
            Assert.Equal(0.3, vm.PositionX);
            Assert.Equal("Title", vm.Text);

            // the restored item is a different instance: writes must reach the live one
            vm.PositionX = 0.9;
            Assert.Equal(0.9, session.Project.Items.First(i => i.Content is TextContent).Transform.X);
        }

        [Fact]
        public void UndoOfAnInspectorEdit_RefreshesTheBoundValues()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Opacity = 0.2;
            raised.Clear();

            session.Undo();

            Assert.Equal(1.0, vm.Opacity);
            Assert.Contains(nameof(SelectedItemViewModel.Opacity), raised);
        }

        [Fact]
        public void DetachingTheSession_StopsListening()
        {
            var (session, vm) = NewInspector(out var screen, out _, out _, out _, out _);
            session.Select(screen.Id);

            vm.Session = null;
            Assert.False(vm.HasSelection);

            // no throw, no re-read: the view model is no longer anyone's listener
            session.EditItem(screen.Id, i => i.Transform.X = 0.7, origin: null);
            Assert.False(vm.HasSelection);
        }
    }
}
