using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// The draw-on-screen hotkeys (issue #110), live while the session has the keyboard. Every
    /// tile on the toolbar has one: the primary lane's are single keys hinted on the tiles (and named
    /// in their tooltips), matching the image editor's tool letters where the tools are shared; the
    /// secondary lane's are named in the tooltips only. Delete removes the select tool's selection,
    /// which has no tile.
    /// <para>
    /// The gestures are written the Windows way; on macOS Control is read as ⌘ (<see cref="SimpleKeyGesture.ForApp"/>).
    /// An instance routes one window's key edges onto an <see cref="IDrawOnScreenController"/>; it
    /// tracks the keys held so an auto-repeat only repeats what is safe to (Undo), never a toggle.
    /// </para>
    /// </summary>
    public sealed class DrawHotkeys
    {
        /// <summary>Stops drawing: the ink stays up and the mouse goes to the desktop, while the keys stay ours.
        /// A frozen session has no desktop to go to, so there it closes.</summary>
        public static SimpleKeyGesture ClickThrough { get; } = new(Key.Escape);
        public static SimpleKeyGesture Hide { get; } = new(Key.H);
        public static SimpleKeyGesture ToggleKeys { get; } = new(Key.K, KeyModifiers.Control);
        public static SimpleKeyGesture Undo { get; } = new(Key.Z, KeyModifiers.Control);
        public static SimpleKeyGesture ClearAll { get; } = new(Key.Delete, KeyModifiers.Control);
        public static SimpleKeyGesture Close { get; } = new(Key.W, KeyModifiers.Control);

        /// <summary>The tool letters. Shared tools take the image editor's (its ToolRegistry); the select
        /// tool is S, since Escape is click-through's here, and the eraser, which the editor lacks, is X.</summary>
        public static SimpleKeyGesture Tool(ToolType tool) => new(tool switch
        {
            ToolType.Pointer => Key.S,
            ToolType.Brush => Key.B,
            ToolType.Arrow => Key.A,
            ToolType.Line => Key.L,
            ToolType.Rectangle => Key.R,
            ToolType.Ellipse => Key.E,
            ToolType.Text => Key.T,
            ToolType.Count => Key.C,
            ToolType.Eraser => Key.X,
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Not a draw-on-screen tool."),
        });

        /// <summary>1–4, thinnest first.</summary>
        public static SimpleKeyGesture Size(int index) => new(Key.D1 + index);

        /// <summary>Shift+1–8, in palette order.</summary>
        public static SimpleKeyGesture Color(int index) => new(Key.D1 + index, KeyModifiers.Shift);

        /// <summary>Every binding, aliases included: Delete and Backspace both delete the selection (a Mac's
        /// delete key is Backspace), and on macOS ⌘Backspace also clears all, as ⌘Delete is a key few
        /// Mac keyboards have.</summary>
        public static IReadOnlyList<(SimpleKeyGesture Gesture, Action<IDrawOnScreenController> Run)> Bindings { get; } = BuildBindings();

        private readonly IDrawOnScreenController _controller;
        private readonly HashSet<Key> _held = new();

        public DrawHotkeys(IDrawOnScreenController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <summary>A key went down. Returns whether a hotkey took it. An auto-repeat of a hotkey is
        /// taken (and swallowed) without running again, except for Undo.</summary>
        public bool KeyDown(Key key, KeyModifiers modifiers)
        {
            var repeat = !_held.Add(key);
            if (Find(key, modifiers) is not { } run)
                return false;

            if (!repeat || Matches(Undo, key, modifiers))
                run(_controller);
            return true;
        }

        public void KeyUp(Key key) => _held.Remove(key);

        /// <summary>Forgets the held keys, for when the window loses the keyboard: their key-ups go elsewhere.</summary>
        public void Reset() => _held.Clear();

        /// <summary>The command bound to <paramref name="key"/> with exactly <paramref name="modifiers"/>, or null.</summary>
        public static Action<IDrawOnScreenController> Find(Key key, KeyModifiers modifiers)
        {
            foreach (var (gesture, run) in Bindings)
            {
                if (Matches(gesture, key, modifiers))
                    return run;
            }

            return null;
        }

        /// <summary>A gesture as a tooltip names it: "Ctrl+Z" (⌘ as "Cmd" on macOS), "Shift+1".</summary>
        public static string Label(SimpleKeyGesture gesture)
        {
            var app = gesture.ForApp();
            var text = app.ToString();

            // SimpleKeyGesture names the top-row digits by their enum ("D1")
            if (app.Key is >= Key.D0 and <= Key.D9)
                text = text[..^2] + (char)('0' + (app.Key - Key.D0));
            return text;
        }

        /// <summary>A gesture's key alone, as a tile's hint shows it: "B", "Esc".</summary>
        public static string Hint(SimpleKeyGesture gesture) => Label(new SimpleKeyGesture(gesture.Key));

        private static bool Matches(SimpleKeyGesture gesture, Key key, KeyModifiers modifiers)
        {
            var app = gesture.ForApp();
            const KeyModifiers relevant = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
            return app.Key == key && app.Modifiers == (modifiers & relevant);
        }

        private static List<(SimpleKeyGesture, Action<IDrawOnScreenController>)> BuildBindings()
        {
            var bindings = new List<(SimpleKeyGesture, Action<IDrawOnScreenController>)>
            {
                (ClickThrough, c =>
                {
                    if (c.State.Frozen)
                        c.Close();
                    else
                        c.StopDrawing();
                }),
                (Hide, c => c.ToggleHide()),
                (ToggleKeys, c => c.ToggleKeys()),
                (Undo, c => c.Undo()),
                (ClearAll, c => c.ClearAll()),
                (Close, c => c.Close()),
                (new SimpleKeyGesture(Key.Delete), c => c.DeleteSelection()),
                (new SimpleKeyGesture(Key.Back), c => c.DeleteSelection()),
            };

            if (OperatingSystem.IsMacOS())
                bindings.Add((new SimpleKeyGesture(Key.Back, KeyModifiers.Control), c => c.ClearAll()));

            foreach (var tool in DrawPalette.Tools)
                bindings.Add((Tool(tool), c => c.PickTool(tool)));

            for (var i = 0; i < DrawPalette.Sizes.Count; i++)
            {
                var index = i;
                bindings.Add((Size(index), c => c.SelectSize(index)));
            }

            for (var i = 0; i < DrawPalette.Colors.Count; i++)
            {
                var index = i;
                bindings.Add((Color(index), c => c.SelectColor(index)));
            }

            return bindings;
        }
    }
}
