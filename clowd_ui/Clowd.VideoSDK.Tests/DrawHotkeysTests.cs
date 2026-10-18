using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Clowd;
using Clowd.UI.DrawOnScreen;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The draw-on-screen hotkey table (issue #110): every tile has a key, no two bindings collide, the
    /// shared tools keep the image editor's letters, and an auto-repeat never re-runs a toggle.
    /// </summary>
    public class DrawHotkeysTests
    {
        [Fact]
        public void Tools_take_the_image_editor_letters_and_S_and_X_for_select_and_eraser()
        {
            var expected = new Dictionary<ToolType, Key>
            {
                [ToolType.Pointer] = Key.S,
                [ToolType.Brush] = Key.B,
                [ToolType.Arrow] = Key.A,
                [ToolType.Line] = Key.L,
                [ToolType.Rectangle] = Key.R,
                [ToolType.Ellipse] = Key.E,
                [ToolType.Text] = Key.T,
                [ToolType.Count] = Key.C,
                [ToolType.Eraser] = Key.X,
            };

            foreach (var tool in DrawPalette.Tools)
                Assert.Equal(new SimpleKeyGesture(expected[tool]), DrawHotkeys.Tool(tool));
        }

        [Fact]
        public void No_two_bindings_share_a_gesture()
        {
            var gestures = DrawHotkeys.Bindings.Select(b => b.Gesture.ForApp()).ToList();
            Assert.Equal(gestures.Count, gestures.Distinct().Count());
        }

        [Fact]
        public void Every_tile_runs_its_command()
        {
            AssertRuns(DrawHotkeys.ClickThrough, "StopDrawing");
            AssertRuns(DrawHotkeys.Hide, "ToggleHide");
            AssertRuns(DrawHotkeys.ToggleKeys, "ToggleKeys");
            AssertRuns(DrawHotkeys.Undo, "Undo");
            AssertRuns(DrawHotkeys.ClearAll, "ClearAll");
            AssertRuns(DrawHotkeys.Close, "Close");
            AssertRuns(new SimpleKeyGesture(Key.Delete), "DeleteSelection");
            AssertRuns(new SimpleKeyGesture(Key.Back), "DeleteSelection");

            foreach (var tool in DrawPalette.Tools)
                AssertRuns(DrawHotkeys.Tool(tool), "PickTool " + tool);
            for (var i = 0; i < DrawPalette.Sizes.Count; i++)
                AssertRuns(DrawHotkeys.Size(i), "SelectSize " + i);
            for (var i = 0; i < DrawPalette.Colors.Count; i++)
                AssertRuns(DrawHotkeys.Color(i), "SelectColor " + i);
        }

        [Fact]
        public void Modifiers_must_match_exactly()
        {
            var controller = new RecordingController();
            var hotkeys = new DrawHotkeys(controller);

            // Ctrl+B is nobody's, and neither is Alt+1
            Assert.False(hotkeys.KeyDown(Key.B, AppControl));
            Assert.False(hotkeys.KeyDown(Key.D1, KeyModifiers.Alt));
            Assert.Empty(controller.Calls);
        }

        [Fact]
        public void A_repeat_reruns_undo_but_not_a_toggle()
        {
            var controller = new RecordingController();
            var hotkeys = new DrawHotkeys(controller);

            Assert.True(hotkeys.KeyDown(Key.H, KeyModifiers.None));
            Assert.True(hotkeys.KeyDown(Key.H, KeyModifiers.None)); // swallowed, not run
            hotkeys.KeyUp(Key.H);
            Assert.True(hotkeys.KeyDown(Key.H, KeyModifiers.None));
            Assert.Equal(new[] { "ToggleHide", "ToggleHide" }, controller.Calls);

            controller.Calls.Clear();
            Assert.True(hotkeys.KeyDown(Key.Z, AppControl));
            Assert.True(hotkeys.KeyDown(Key.Z, AppControl));
            Assert.Equal(new[] { "Undo", "Undo" }, controller.Calls);
        }

        [Fact]
        public void Reset_forgets_held_keys()
        {
            var controller = new RecordingController();
            var hotkeys = new DrawHotkeys(controller);

            hotkeys.KeyDown(Key.H, KeyModifiers.None);
            hotkeys.Reset(); // the key-up went to another window
            hotkeys.KeyDown(Key.H, KeyModifiers.None);
            Assert.Equal(new[] { "ToggleHide", "ToggleHide" }, controller.Calls);
        }

        [Fact]
        public void Labels_read_as_the_keys_are_printed()
        {
            var ctrl = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
            Assert.Equal("Esc", DrawHotkeys.Hint(DrawHotkeys.ClickThrough));
            Assert.Equal("B", DrawHotkeys.Hint(DrawHotkeys.Tool(ToolType.Brush)));
            Assert.Equal("1", DrawHotkeys.Label(DrawHotkeys.Size(0)));
            Assert.Equal("Shift+8", DrawHotkeys.Label(DrawHotkeys.Color(7)));
            Assert.Equal(ctrl + "+Z", DrawHotkeys.Label(DrawHotkeys.Undo));
            Assert.Equal(ctrl + "+Del", DrawHotkeys.Label(DrawHotkeys.ClearAll));
        }

        // Control, or ⌘ on macOS, where the table reads Control as Meta
        private static KeyModifiers AppControl => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        private static void AssertRuns(SimpleKeyGesture gesture, string call)
        {
            var controller = new RecordingController();
            var app = gesture.ForApp();
            Assert.True(new DrawHotkeys(controller).KeyDown(app.Key, app.Modifiers), $"{gesture} is not bound");
            Assert.Equal(new[] { call }, controller.Calls);
        }

        private sealed class RecordingController : IDrawOnScreenController
        {
            public List<string> Calls { get; } = new();
            public DrawOnScreenState State => DrawOnScreenState.Initial;
            public bool IsFocused => true;
            public bool CanUndo => true;
            public bool CanClear => true;
            public event EventHandler Changed { add { } remove { } }
            public void PickTool(ToolType tool) => Calls.Add("PickTool " + tool);
            public void ToggleClickThrough() => Calls.Add("ToggleClickThrough");
            public void StopDrawing() => Calls.Add("StopDrawing");
            public void ToggleHide() => Calls.Add("ToggleHide");
            public void ToggleKeys() => Calls.Add("ToggleKeys");
            public void SelectColor(int index) => Calls.Add("SelectColor " + index);
            public void SelectSize(int index) => Calls.Add("SelectSize " + index);
            public void Undo() => Calls.Add("Undo");
            public void ClearAll() => Calls.Add("ClearAll");
            public void DeleteSelection() => Calls.Add("DeleteSelection");
            public void Close() => Calls.Add("Close");
        }
    }
}
