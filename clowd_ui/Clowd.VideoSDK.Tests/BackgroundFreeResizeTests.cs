using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Clowd.UI.VideoEditor;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// A wallpaper's free two-axis resize, tested along the path the editor actually takes rather
    /// than the view model in isolation. A background is meant to be Unlocked from the instant it
    /// exists: Width % and Height % in the panel, eight handles on the gizmo, and a corner drag
    /// that sizes each axis on its own. The first cut of the feature shipped with an inspector
    /// test that passed while the app showed one "Size" row and locked corners, because it asked
    /// the view model about an item the session had just added and nothing else. What actually
    /// runs in the editor is: the window's <c>RevealNewItem</c> (or a click on the timeline) calls
    /// <c>EditorSession.Select</c>; the session raises SelectionChanged; the view model re-reads
    /// the model in <c>Sync</c>; the window mirrors <c>AspectUnlocked</c> into
    /// <c>TransformGizmoControl.FreeResize</c> on a PropertyChanged; and the gizmo decides what a
    /// corner drag does from that flag plus the item. These tests walk that chain with real
    /// sessions, real project files (JSON round trips) and the real decision function
    /// (<see cref="GizmoMath.CornerMode"/>) the gizmo switches on. The one thing they cannot do
    /// is drive an Avalonia pointer: the gizmo is a control whose constructor needs a platform
    /// cursor factory, so its press/move handlers still need a human. Framework-free like
    /// <see cref="BackgroundInspectorTests"/>.
    /// </summary>
    public class BackgroundFreeResizeTests
    {
        private const double CanvasW = 1920, CanvasH = 1080;
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>An empty project: what the tool-strip button adds a wallpaper to.</summary>
        private static Project EmptyProject() => new Project
        {
            Output = new OutputSettings { WidthPx = (int)CanvasW, HeightPx = (int)CanvasH, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        /// <summary>A project file written before <c>AddBackground</c> seeded the explicit height,
        /// or edited by hand: the wallpaper's transform stores no ScaleY at all. Built from the
        /// model and pushed through the real serializer so it is the file the editor would open,
        /// not an in-memory object graph that happens to look like one.</summary>
        private static (Project Project, Guid ItemId) ProjectFileWithoutScaleY()
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Background", Order = 0 };
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = new BackgroundContent(),
                Transform = new Transform(), // the default placement, height derived
            };
            var project = EmptyProject();
            project.Tracks.Add(track);
            project.Items.Add(item);

            var json = project.ToJson();
            Assert.DoesNotContain("ScaleY", json); // a null is not written, so the file has none
            return (Project.FromJson(json), item.Id);
        }

        /// <summary>The window's wiring, verbatim: the inspector's Unlocked flag reaches the gizmo
        /// only through this PropertyChanged, so a test that reads <c>AspectUnlocked</c> directly
        /// would pass on a flag the gizmo never received.</summary>
        private static Func<bool> MirrorLikeTheWindow(SelectedItemViewModel vm)
        {
            var gizmoFreeResize = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SelectedItemViewModel.AspectUnlocked))
                    gizmoFreeResize = vm.AspectUnlocked;
            };
            return () => gizmoFreeResize;
        }

        private static Item LiveItem(EditorSession session, Guid id)
            => session.Project.Items.First(i => i.Id == id);

        private static void AssertUnlockedFromTheStart(EditorSession session, SelectedItemViewModel vm,
            Func<bool> gizmoFreeResize, ICollection<string> raised, Guid id)
        {
            var item = LiveItem(session, id);

            // the panel: two rows, labelled Width and Height, and the notifications that move the
            // bindings off whatever the previous selection showed
            Assert.True(vm.ShowScale);
            Assert.True(vm.ShowScaleHeight);
            Assert.Equal("Width", vm.ScaleLabel);
            Assert.Contains(nameof(SelectedItemViewModel.ShowScaleHeight), raised);
            Assert.Contains(nameof(SelectedItemViewModel.ScaleLabel), raised);

            // the tile the gizmo's edge handles hang off, as the window saw it
            Assert.True(vm.AspectUnlocked);
            Assert.True(gizmoFreeResize());

            // the corner decision the gizmo makes on the first pointer move, from the flag it was
            // forwarded and the live item: each axis on its own
            Assert.Equal(CornerResizeMode.Free, GizmoMath.CornerMode(gizmoFreeResize(), item));
            Assert.True(GizmoMath.IsFreeByContent(item));
        }

        // ------------------------------------------------------------------ the two arrival paths

        /// <summary>The tool-strip path: <c>VideoEditorWindow.AddBackground</c> calls
        /// <c>EditorSession.AddBackground</c> then <c>RevealNewItem</c>, which selects the item
        /// through the session. The view model is created and pointed at the session before the
        /// item exists, as the window's is, and the previous selection is a ratio-locked picture,
        /// so every flag has to move rather than merely already be right.</summary>
        [Fact]
        public void AddedFromTheToolStrip_IsUnlockedTheMomentItIsSelected()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var gizmoFreeResize = MirrorLikeTheWindow(vm);

            // the selection before: a text card, ratio-locked (Original) with one Size row
            var text = session.AddText(0, Ms(5_000));
            session.Select(text.Id);
            Assert.False(vm.AspectUnlocked);
            Assert.False(gizmoFreeResize());

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var added = session.AddBackground(0, Ms(5_000));
            Assert.NotNull(added);
            session.Select(added.Id); // RevealNewItem

            AssertUnlockedFromTheStart(session, vm, gizmoFreeResize, raised, added.Id);
            Assert.Equal(1.0, LiveItem(session, added.Id).Transform.ScaleY);
            Assert.Equal(1.0, vm.Scale);
            Assert.Equal(1.0, vm.ScaleHeight);
        }

        /// <summary>The file path: a project saved before the seed existed (or edited by hand) is
        /// opened and its wallpaper clicked. Opening it is what fills the height (see
        /// <see cref="OpeningALegacyProject_GivesEveryBackgroundTheExplicitHeight"/>), so by the
        /// time the item is selected the model spells Unlocked the same way a fresh item does, and
        /// the inspector and the gizmo read the same state off it.</summary>
        [Fact]
        public void LoadedFromAFileWithoutScaleY_IsUnlockedTheMomentItIsSelected()
        {
            var (project, id) = ProjectFileWithoutScaleY();
            var session = new EditorSession(project, null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var gizmoFreeResize = MirrorLikeTheWindow(vm);
            // the migration ran in the session's constructor: the model value, not a panel rule
            Assert.Equal(1.0, LiveItem(session, id).Transform.ScaleY);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            session.Select(id);

            AssertUnlockedFromTheStart(session, vm, gizmoFreeResize, raised, id);
            Assert.Equal(1.0, vm.ScaleHeight);
        }

        // ------------------------------------------------------------------ the legacy migration

        /// <summary>Opening a project is <c>new EditorSession(project, ...)</c>, whose constructor
        /// runs <c>Project.Normalize</c> before the first undo snapshot. A legacy wallpaper comes
        /// out of it carrying the height the composer was already drawing for it (Scale), so the
        /// model, not a view-model rule, says it is free-sized; a null transform is materialized
        /// to the default placement for the same reason.</summary>
        [Fact]
        public void OpeningALegacyProject_GivesEveryBackgroundTheExplicitHeight()
        {
            var (project, id) = ProjectFileWithoutScaleY();
            // a second legacy item at a non-default width, so the fill is seen to copy Scale
            // rather than write a constant, and a third with no transform at all
            var track = project.Tracks[0];
            var narrow = new Item
            {
                Id = Guid.NewGuid(), TrackId = track.Id, TimelineStartTicks = 0, DurationTicks = Ms(5_000),
                Content = new BackgroundContent(), Transform = new Transform { Scale = 0.6 },
            };
            var bare = new Item
            {
                Id = Guid.NewGuid(), TrackId = track.Id, TimelineStartTicks = 0, DurationTicks = Ms(5_000),
                Content = new BackgroundContent(),
            };
            project.Items.Add(narrow);
            project.Items.Add(bare);
            project = Project.FromJson(project.ToJson());
            Assert.All(project.Items, i => Assert.Null(i.Transform?.ScaleY));

            var session = new EditorSession(project, null, null);

            Assert.Equal(1.0, LiveItem(session, id).Transform.ScaleY);
            Assert.Equal(0.6, LiveItem(session, narrow.Id).Transform.ScaleY);
            var bareTransform = LiveItem(session, bare.Id).Transform;
            Assert.NotNull(bareTransform);
            Assert.Equal(1.0, bareTransform.ScaleY);
            Assert.Equal(1.0, bareTransform.Scale);
            Assert.Equal(0.5, bareTransform.X);
            Assert.Equal(0.5, bareTransform.Y);
            // opening is not an edit: nothing to undo, nothing marked changed
            Assert.False(session.CanUndo);
        }

        /// <summary>The fill is <c>ScaleY ??= Scale</c>: it writes only where nothing is stored
        /// and nothing in Normalize clears a stored one, so the other callers (TimelineOps after a
        /// split or trim, RecordingProject's bootstrap) can run it any number of times without
        /// dragging a height the user has set back to the width, or re-deriving one after the
        /// width moved.</summary>
        [Fact]
        public void Normalize_IsIdempotent_AndNeverTouchesAStoredHeight()
        {
            var (project, id) = ProjectFileWithoutScaleY();
            var item = project.Items.First(i => i.Id == id);
            item.Transform.Scale = 0.6;

            project.Normalize();
            Assert.Equal(0.6, item.Transform.ScaleY);

            // the width moves on, the height it once copied stays
            item.Transform.Scale = 0.3;
            project.Normalize();
            project.Normalize();
            Assert.Equal(0.6, item.Transform.ScaleY);

            // and a height the user set is the user's
            item.Transform.ScaleY = 0.9;
            project.Normalize();
            Assert.Equal(0.9, item.Transform.ScaleY);

            // a picture is never touched: its null height means "follow the content"
            var text = new Item { Id = Guid.NewGuid(), TrackId = project.Tracks[0].Id, DurationTicks = Ms(1_000),
                Content = new TextContent { Text = "x" }, Transform = new Transform { Scale = 0.4 } };
            project.Items.Add(text);
            project.Normalize();
            Assert.Null(text.Transform.ScaleY);
        }

        /// <summary>The wrong render the migration exists to stop. Before it, a legacy wallpaper
        /// drew its height from <c>ScaleY ?? Scale</c>, so setting Width alone (the inspector's
        /// Width row here; the gizmo's side handles write the same thing) also halved the drawn
        /// height while the Height row went on saying 100%. Asserted on composed pixels: after
        /// the same Width edit, a legacy item and a freshly added one must draw byte-identical
        /// frames, and that frame must not be the one a halved height draws. Remove the fill in
        /// <c>Project.Normalize</c> and the first assertion fails.</summary>
        [Fact]
        public void WritingWidthAlone_OnALegacyBackground_LeavesTheDrawnHeightAlone()
        {
            const int w = 64, h = 64;

            // legacy: a file without ScaleY, opened, Width set to 50% through the panel
            var (legacyProject, legacyId) = ProjectFileWithoutScaleY();
            legacyProject.Output.WidthPx = w;
            legacyProject.Output.HeightPx = h;
            var legacy = new EditorSession(legacyProject, null, null);
            var legacyVm = new SelectedItemViewModel { Session = legacy };
            legacy.Select(legacyId);
            legacyVm.Scale = 0.5;
            Assert.Equal(0.5, LiveItem(legacy, legacyId).Transform.Scale);
            Assert.Equal(1.0, LiveItem(legacy, legacyId).Transform.ScaleY);
            Assert.Equal(1.0, legacyVm.ScaleHeight); // what the panel says

            // fresh: the tool-strip item in a project of the same size, the same edit
            var freshProject = EmptyProject();
            freshProject.Output.WidthPx = w;
            freshProject.Output.HeightPx = h;
            var fresh = new EditorSession(freshProject, null, null);
            var freshVm = new SelectedItemViewModel { Session = fresh };
            var added = fresh.AddBackground(0, Ms(5_000));
            fresh.Select(added.Id);
            freshVm.Scale = 0.5;

            var legacyPixels = Render(legacy.Project, w, h);
            var freshPixels = Render(fresh.Project, w, h);
            Assert.Equal(0, MaxChannelDifference(legacyPixels, freshPixels));

            // the frame the bug drew: half the width AND half the height. Composed from the same
            // legacy project with the height forced back to what a null would resolve to, to prove
            // the comparison above can tell the two apart.
            legacy.EditItem(legacyId, i => i.Transform.ScaleY = 0.5, origin: new object());
            var halvedPixels = Render(legacy.Project, w, h);
            Assert.NotEqual(0, MaxChannelDifference(legacyPixels, halvedPixels));

            // and the shape of the difference is the height: the top-center column is painted at
            // full height and bare at half height, while the left edge is bare in both
            Assert.False(IsBlack(Px(legacyPixels, w / 2, 1, w)));
            Assert.True(IsBlack(Px(halvedPixels, w / 2, 1, w)));
            Assert.True(IsBlack(Px(legacyPixels, 1, h / 2, w)));
        }

        private static byte[] Render(Project project, int w, int h)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(w, h);
            FrameComposer.Compose(project, 0, null, surface.Canvas, w, h);

            int rowBytes = w * 4;
            var native = Marshal.AllocHGlobal(rowBytes * h);
            try
            {
                Assert.True(factory.TryReadPixels(surface, w, h, native, rowBytes));
                var pixels = new byte[rowBytes * h];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int x, int y, int w)
        {
            int i = y * w * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        private static bool IsBlack((byte B, byte G, byte R, byte A) px, int tolerance = 2)
            => px.R <= tolerance && px.G <= tolerance && px.B <= tolerance;

        private static int MaxChannelDifference(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            int max = 0;
            for (int i = 0; i < a.Length; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        /// <summary>A wallpaper added by this build keeps its state through a save and reload:
        /// the seeded height is written to the file and read back, so the next session opens it
        /// already Unlocked without the content rule having to cover for it.</summary>
        [Fact]
        public void AddedThenSavedAndReloaded_StillCarriesTheExplicitHeight()
        {
            var first = new EditorSession(EmptyProject(), null, null);
            var added = first.AddBackground(0, Ms(5_000));
            var json = first.Project.ToJson();
            Assert.Contains("ScaleY", json);

            var session = new EditorSession(Project.FromJson(json), null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var gizmoFreeResize = MirrorLikeTheWindow(vm);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            session.Select(added.Id);

            Assert.Equal(1.0, LiveItem(session, added.Id).Transform.ScaleY);
            AssertUnlockedFromTheStart(session, vm, gizmoFreeResize, raised, added.Id);
        }

        // ------------------------------------------------------------------ the corner drag

        /// <summary>The gizmo's corner branch used to be the last of a chain of guards, and a
        /// wallpaper without an explicit height fell through it to the ratio-holding branch even
        /// with all eight handles showing. The decision is now one function; this pins its table
        /// for a wallpaper against a picture, including the stale-flag guard a picture keeps.</summary>
        [Fact]
        public void CornerMode_IsFreeForAWallpaper_WhateverTheFlagOrTheModelSay()
        {
            var withHeight = new Item { Content = new BackgroundContent(), Transform = new Transform { ScaleY = 1.0 } };
            var withoutHeight = new Item { Content = new BackgroundContent(), Transform = new Transform() };
            var noTransform = new Item { Content = new BackgroundContent() };

            foreach (var item in new[] { withHeight, withoutHeight, noTransform })
            {
                Assert.Equal(CornerResizeMode.Free, GizmoMath.CornerMode(freeResizeTile: true, item));
                // even if the window's forwarding never reached the gizmo
                Assert.Equal(CornerResizeMode.Free, GizmoMath.CornerMode(freeResizeTile: false, item));
                Assert.True(GizmoMath.IsFreeByContent(item));
            }

            // a picture: free only on the Unlocked tile AND with the height that tile writes
            var pictureUnlocked = new Item { Content = new SolidContent(), Transform = new Transform { ScaleY = 0.5 } };
            var pictureLocked = new Item { Content = new SolidContent(), Transform = new Transform() };
            Assert.Equal(CornerResizeMode.Free, GizmoMath.CornerMode(true, pictureUnlocked));
            Assert.Equal(CornerResizeMode.BoxAspect, GizmoMath.CornerMode(false, pictureUnlocked));
            Assert.Equal(CornerResizeMode.ContentAspect, GizmoMath.CornerMode(true, pictureLocked));
            Assert.Equal(CornerResizeMode.ContentAspect, GizmoMath.CornerMode(false, pictureLocked));
            Assert.False(GizmoMath.IsFreeByContent(pictureUnlocked));
            Assert.Equal(CornerResizeMode.ContentAspect, GizmoMath.CornerMode(true, null));
        }

        /// <summary>A bottom-right corner drag on a file-loaded wallpaper, with the geometry the
        /// gizmo captures at press time (<see cref="ItemPlacement.TryResolve"/>'s denominators)
        /// and the math the Free branch runs (<see cref="GizmoMath.ResizeFree"/>): the two scales
        /// come out of the pointer apart, and once written the panel still reads Width/Height.
        /// A ratio-holding drag would have moved the height with the width.</summary>
        [Fact]
        public void ACornerDrag_OnAFileLoadedWallpaper_SizesEachAxisOnItsOwn()
        {
            var (project, id) = ProjectFileWithoutScaleY();
            var session = new EditorSession(project, null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var gizmoFreeResize = MirrorLikeTheWindow(vm);
            session.Select(id);
            var item = LiveItem(session, id);

            // press: the whole canvas, anchored at its top-left, the canvas as both denominators
            Assert.True(ItemPlacement.TryResolve(session.Project, item, CanvasW, CanvasH, out var placed));
            Assert.Equal(CanvasW, placed.ScaleDenominatorPx);
            Assert.Equal(CanvasH, placed.ScaleDenominatorYPx);
            Assert.Equal(CornerResizeMode.Free, GizmoMath.CornerMode(gizmoFreeResize(), item));

            // move: the pointer lands at half the width and three quarters of the height
            var (scaleX, scaleY, x, y) = GizmoMath.ResizeFree(
                pointerX: CanvasW / 2, pointerY: CanvasH * 3 / 4,
                anchorX: placed.X, anchorY: placed.Y, draggingRight: true, draggingDown: true,
                placed.ScaleDenominatorPx, placed.ScaleDenominatorYPx,
                0, 0, CanvasW, CanvasH,
                SelectedItemViewModel.MinScale, SelectedItemViewModel.MaxScale);
            Assert.Equal(0.5, scaleX, 6);
            Assert.Equal(0.75, scaleY, 6);
            Assert.Equal(0.25, x, 6);
            Assert.Equal(0.375, y, 6);

            // the write the Free branch makes, through the session like WriteRow does (a foreign
            // origin, so the view model re-reads it), and the panel afterwards
            session.EditItem(id, i =>
            {
                i.Transform.Scale = scaleX;
                i.Transform.ScaleY = scaleY;
                i.Transform.X = x;
                i.Transform.Y = y;
            }, "gizmo:resize", origin: new object());
            Assert.Equal(0.5, LiveItem(session, id).Transform.Scale);
            Assert.Equal(0.75, LiveItem(session, id).Transform.ScaleY);
            Assert.Equal(0.5, vm.Scale);
            Assert.Equal(0.75, vm.ScaleHeight);
            Assert.True(vm.ShowScaleHeight);
            Assert.Equal("Width", vm.ScaleLabel);
            Assert.True(vm.AspectUnlocked);
        }

        // ------------------------------------------------------------------ the Height reset dot

        /// <summary>What the Height row's reset dot does through its TwoWay binding: a stretched
        /// wallpaper goes back to the full canvas height, written as an explicit 1, not a cleared
        /// height (which would read as Original on a picture and hide the row).</summary>
        [Fact]
        public void ResettingHeightToOne_WritesAnExplicitHeightOfOne()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var added = session.AddBackground(0, Ms(5_000));
            session.Select(added.Id);

            vm.ScaleHeight = 0.8;
            Assert.Equal(0.8, LiveItem(session, added.Id).Transform.ScaleY);

            vm.ScaleHeight = 1; // ResetDefaultButton DefaultValue="1"
            Assert.Equal(1.0, LiveItem(session, added.Id).Transform.ScaleY);
            Assert.True(vm.ShowScaleHeight);
            Assert.Equal("Width", vm.ScaleLabel);
        }
    }
}
