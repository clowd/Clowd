using System;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Covers the opt-in Hidden/Locked flags on <see cref="GraphicBase"/> and the
    /// <see cref="GraphicCollection.StructureChanged"/> panel signal:
    /// - Hidden graphics drop out of rendering, export sizing (ContentBounds) and hit-testing;
    /// - Locked graphics stay transparent to canvas hit-testing but still render and count toward
    ///   bounds;
    /// - both flags persist through the serializer / history engine and default false for old
    ///   documents that predate the fields;
    /// - StructureChanged fires on every membership/order mutation, including reorders that leave
    ///   the collection count unchanged.
    /// </summary>
    public class HiddenLockedTests
    {
        static HiddenLockedTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static GraphicRectangle Rect(double x, double y, double w, double h, Color? color = null) =>
            new GraphicRectangle(color ?? Colors.Red, 2, new Rect(x, y, w, h));

        // ====================================================================
        // Hidden enforcement
        // ====================================================================

        [AvaloniaFact]
        public void Hidden_ExcludedFromContentBounds()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var near = Rect(0, 0, 50, 50);
            var far = Rect(500, 500, 50, 50, Colors.Blue);
            canvas.GraphicsList.Add(near);
            canvas.GraphicsList.Add(far);

            var withBoth = canvas.GraphicsList.ContentBounds;
            Assert.True(withBoth.Right > 500, "sanity: the far graphic should extend the union past 500");

            far.Hidden = true;
            var withoutFar = canvas.GraphicsList.ContentBounds;
            Assert.True(withoutFar.Right < 100, "a hidden graphic must not contribute to content/export bounds");
        }

        [AvaloniaFact]
        public void Hidden_ShrinksExportBitmap()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            canvas.GraphicsList.Add(Rect(0, 0, 40, 40));
            canvas.GraphicsList.Add(Rect(400, 0, 40, 40, Colors.Blue));

            using var wide = canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.White);
            int wideWidth = wide.PixelSize.Width;

            canvas.GraphicsList[1].Hidden = true;

            using var narrow = canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.White);
            Assert.True(narrow.PixelSize.Width < wideWidth, "hiding the right-most graphic must shrink the export bitmap");
        }

        [AvaloniaFact]
        public void Hidden_IsNotHitTested()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = Rect(10, 10, 80, 60);
            canvas.GraphicsList.Add(rect);
            var inside = new Point(50, 40);

            Assert.Same(rect, canvas.ToolPointer.MakeHitTest(canvas, inside, out _));

            rect.Hidden = true;
            Assert.Null(canvas.ToolPointer.MakeHitTest(canvas, inside, out _));
        }

        [AvaloniaFact]
        public void Hidden_TogglesThroughHistory_AndUndoRestores()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = Rect(10, 10, 50, 40);
            canvas.GraphicsList.Add(rect);
            canvas.AddCommandToHistory(false); // baseline
            Assert.False(rect.Hidden);

            canvas.ToggleHidden(rect);
            Assert.True(rect.Hidden);

            // Hidden is a persisted field flowing through the delta engine, so undo restores it
            Assert.True(canvas.CommandUndo.CanExecute(null));
            canvas.CommandUndo.Executed(null);
            Assert.False(rect.Hidden);
        }

        [AvaloniaFact]
        public void ToggleHidden_UnselectsWhenBecomingHidden()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = Rect(10, 10, 50, 40);
            canvas.GraphicsList.Add(rect);
            rect.IsSelected = true;

            canvas.ToggleHidden(rect);
            Assert.True(rect.Hidden);
            Assert.False(rect.IsSelected); // a graphic becoming hidden is dropped from the selection
        }

        // ====================================================================
        // Locked enforcement
        // ====================================================================

        [AvaloniaFact]
        public void Locked_IsNotHitTested()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = Rect(10, 10, 80, 60);
            canvas.GraphicsList.Add(rect);
            var inside = new Point(50, 40);

            Assert.Same(rect, canvas.ToolPointer.MakeHitTest(canvas, inside, out _));

            rect.Locked = true;
            Assert.Null(canvas.ToolPointer.MakeHitTest(canvas, inside, out _));
        }

        [AvaloniaFact]
        public void Locked_StillContributesToContentBounds()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = Rect(0, 0, 50, 50);
            canvas.GraphicsList.Add(rect);

            var before = canvas.GraphicsList.ContentBounds;
            rect.Locked = true;
            var after = canvas.GraphicsList.ContentBounds;

            Assert.Equal(before, after); // locking is a hit-test-only flag; it still renders/exports/bounds
        }

        [AvaloniaFact]
        public void Locked_CanStillBeSelectedProgrammatically()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = Rect(0, 0, 50, 50);
            canvas.GraphicsList.Add(rect);
            rect.Locked = true;

            // the Layers panel selects a locked graphic through the seam (bypasses the hit test)
            canvas.SetPanelSelection(rect, additive: false);
            Assert.True(rect.IsSelected);
        }

        // ====================================================================
        // Serialization
        // ====================================================================

        [AvaloniaFact]
        public void HiddenAndLocked_RoundTripThroughSerializer()
        {
            var g = Rect(1, 2, 30, 40);
            g.Hidden = true;
            g.Locked = true;

            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { g });
            var restored = GraphicsSerializer.DeserializeFromUtf8Bytes(bytes);

            var single = Assert.Single(restored);
            Assert.True(single.Hidden);
            Assert.True(single.Locked);
        }

        [AvaloniaFact]
        public void OldPayloadWithoutFields_DeserializesWithDefaultsFalse()
        {
            // simulate a pre-feature graphics payload by stripping the new fields from a fresh one
            var g = Rect(1, 2, 30, 40);
            g.Hidden = true;
            g.Locked = true;

            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { g });
            var array = (JsonArray)JsonNode.Parse(bytes);
            var obj = (JsonObject)array[0];

            // sanity: the new fields ARE persisted for current documents
            Assert.True(obj.ContainsKey("hidden"));
            Assert.True(obj.ContainsKey("locked"));

            obj.Remove("hidden");
            obj.Remove("locked");

            var stripped = Encoding.UTF8.GetBytes(array.ToJsonString());
            var restored = GraphicsSerializer.DeserializeFromUtf8Bytes(stripped);

            var single = Assert.Single(restored);
            Assert.False(single.Hidden); // absent field keeps the field-initializer default
            Assert.False(single.Locked);
        }

        // ====================================================================
        // StructureChanged
        // ====================================================================

        [AvaloniaFact]
        public void StructureChanged_FiresOnAddAndRemove()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            int raises = 0;
            canvas.GraphicsList.StructureChanged += (_, _) => raises++;

            var a = Rect(0, 0, 10, 10);
            canvas.GraphicsList.Add(a);
            Assert.True(raises >= 1); // Add is a structural mutation

            raises = 0;
            canvas.GraphicsList.Remove(a);
            Assert.True(raises >= 1); // Remove is a structural mutation
        }

        [AvaloniaFact]
        public void StructureChanged_FiresOnReorder_WhereCountValueDoesNot()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var a = Rect(0, 0, 10, 10);
            var b = Rect(20, 0, 10, 10, Colors.Blue);
            var c = Rect(40, 0, 10, 10, Colors.Green);
            canvas.GraphicsList.Add(a);
            canvas.GraphicsList.Add(b);
            canvas.GraphicsList.Add(c);

            int structRaises = 0;
            canvas.GraphicsList.StructureChanged += (_, _) => structRaises++;

            int countBefore = canvas.GraphicsList.Count;
            Assert.Same(a, canvas.GraphicsList[0]);

            canvas.MoveGraphicToIndex(a, canvas.GraphicsList.Count - 1);

            Assert.True(structRaises >= 1, "a reorder must raise StructureChanged");
            Assert.Equal(countBefore, canvas.GraphicsList.Count); // the reorder nets no count change
            Assert.Same(a, canvas.GraphicsList[canvas.GraphicsList.Count - 1]); // a moved to the end
        }
    }
}
