using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace Clowd.Config
{
    /// <summary>
    /// Whether Draw on Screen exists as a feature. Rendered by the Draw on Screen page as the tile
    /// selector at its top (<see cref="ModeSelectorAttribute"/>). Off removes every way to open it —
    /// the tray item, the hotkey and the recording and share strips' draw buttons — and hides the
    /// rest of the page. Persisted by name.
    /// </summary>
    public enum DrawOnScreenMode
    {
        [Description("On")]
        [ModeCaption("Draw over your screen from the tray menu, a hotkey, or while recording or sharing a region.")]
        On,

        [Description("Off")]
        [ModeCaption("Turns Draw on Screen off entirely. Its tray item, hotkey and draw buttons disappear.")]
        Off,
    }

    /// <summary>Which part of the screen the keystroke overlay sits in. Declaration order is
    /// dropdown order: the grid read row by row.</summary>
    public enum DrawKeystrokeRegion
    {
        [Description("Top left")] TopLeft,
        [Description("Top")] Top,
        [Description("Top right")] TopRight,
        [Description("Left")] Left,
        [Description("Center")] Center,
        [Description("Right")] Right,
        [Description("Bottom left")] BottomLeft,
        [Description("Bottom")] Bottom,
        [Description("Bottom right")] BottomRight,
    }

    /// <summary>
    /// Which keystrokes the overlay shows. Mirrors the video editor's
    /// <c>Clowd.VideoSDK.Model.KeystrokeFilter</c> member for member, mapped inside Clowd.Ui: this
    /// assembly does not reference the SDK.
    /// </summary>
    public enum DrawKeystrokeFilter
    {
        [Description("None")] None,
        [Description("Special keys")] Special,
        [Description("Shortcuts")] Shortcuts,
    }

    /// <summary>How a keystroke row arrives or leaves. The visual members of the SDK's
    /// <c>TransitionKind</c>, by the same names (mapped inside Clowd.Ui).</summary>
    public enum DrawKeystrokeAnimation
    {
        [Description("None")] None,
        [Description("Fade")] Fade,
        [Description("Slide left")] SlideLeft,
        [Description("Slide right")] SlideRight,
        [Description("Slide up")] SlideUp,
        [Description("Slide down")] SlideDown,
        [Description("Wipe")] Wipe,
    }

    /// <summary>The SDK's <c>TransitionEasing</c>, by the same names (mapped inside Clowd.Ui).</summary>
    public enum DrawKeystrokeEasing
    {
        [Description("Linear")] Linear,
        [Description("Cubic in")] CubicIn,
        [Description("Cubic out")] CubicOut,
        [Description("Cubic in-out")] CubicInOut,
    }

    /// <summary>
    /// Settings for Draw on Screen, and for the keystroke overlay its toolbar's keyboard button
    /// turns on. The keystroke rows are the video editor's keyboard track drawn live, so the
    /// settings are that track's KEYSTROKES, placement and ANIMATION inspector rows with the same
    /// defaults — except that a screen region stands in for the track's Center X/Center Y.
    /// <para>Every value is read live by the open session (<c>DrawOnScreenSession</c>), so an edit
    /// here shows on the next keystroke.</para>
    /// </summary>
    public class SettingsDrawOnScreen : SimpleNotifyObject
    {
        /// <summary>The tile selector at the top of the page; Off hides the rest of the page and
        /// every Draw on Screen entry point in the app (see <see cref="DrawOnScreenMode"/>).</summary>
        [ModeSelector]
        public DrawOnScreenMode Mode
        {
            get => _mode;
            set => Set(ref _mode, value, nameof(Mode), nameof(IsEnabled));
        }

        /// <summary>Whether Draw on Screen is offered anywhere (<see cref="Mode"/> is On).</summary>
        [Browsable(false), JsonIgnore]
        public bool IsEnabled => Mode == DrawOnScreenMode.On;

        /// <summary>The toolbar's keyboard button, remembered so the next session opens the way the
        /// last one was left.</summary>
        [Browsable(false)]
        public bool ShowKeystrokes
        {
            get => _showKeystrokes;
            set => Set(ref _showKeystrokes, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Screen region")]
        [Description("Where on the screen pressed keys are shown. They appear on the screen the mouse is on.")]
        public DrawKeystrokeRegion Region
        {
            get => _region;
            set => Set(ref _region, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Margin")]
        [Description("Gap between the keys and the edge of the screen, in pixels. The taskbar or menu bar is already kept clear.")]
        [Range(0, 1000)]
        public int Margin
        {
            get => _margin;
            set => Set(ref _margin, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Width")]
        [Description("How wide a row of keys may grow, as a share of the screen width, before it wraps.")]
        [Range(0.0, 1.0)]
        public double Width
        {
            get => _width;
            set => Set(ref _width, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Opacity")]
        [Range(0.0, 1.0)]
        public double Opacity
        {
            get => _opacity;
            set => Set(ref _opacity, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Filter")]
        [Description("Which keys are shown: everything typed, only special keys and shortcuts (Esc, F5, Ctrl+C…), or only shortcuts.")]
        public DrawKeystrokeFilter Filter
        {
            get => _filter;
            set => Set(ref _filter, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Size")]
        [Description("Text size of the shown keys, in pixels.")]
        [Range(8, 200)]
        public int FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Linger")]
        [Description("How long a row stays on screen after its last key, in milliseconds.")]
        [Range(0, 10000)]
        public int LingerMs
        {
            get => _lingerMs;
            set => Set(ref _lingerMs, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("New row")]
        [Description("A pause in typing longer than this, in milliseconds, starts a new row.")]
        [Range(0, 10000)]
        public int PauseBreakMs
        {
            get => _pauseBreakMs;
            set => Set(ref _pauseBreakMs, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Text")]
        [Description("Color of typed text. Special keys keep their own keycap look.")]
        public Color TextColor
        {
            get => _textColor;
            set => Set(ref _textColor, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Fill")]
        [Description("Background behind typed text. Its transparency is part of the color.")]
        public Color FillColor
        {
            get => _fillColor;
            set => Set(ref _fillColor, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Entry")]
        [Description("How each new row arrives.")]
        public DrawKeystrokeAnimation EntryKind
        {
            get => _entryKind;
            set => Set(ref _entryKind, value, nameof(EntryKind), nameof(EntryAnimates));
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Entry length")]
        [Description("In milliseconds.")]
        [Range(0, 10000)]
        [DisabledWhen(nameof(EntryAnimates), false)]
        public int EntryDurationMs
        {
            get => _entryDurationMs;
            set => Set(ref _entryDurationMs, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Entry easing")]
        [DisabledWhen(nameof(EntryAnimates), false)]
        public DrawKeystrokeEasing EntryEasing
        {
            get => _entryEasing;
            set => Set(ref _entryEasing, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Exit")]
        [Description("How a row leaves once its linger is up.")]
        public DrawKeystrokeAnimation ExitKind
        {
            get => _exitKind;
            set => Set(ref _exitKind, value, nameof(ExitKind), nameof(ExitAnimates));
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Exit length")]
        [Description("In milliseconds.")]
        [Range(0, 10000)]
        [DisabledWhen(nameof(ExitAnimates), false)]
        public int ExitDurationMs
        {
            get => _exitDurationMs;
            set => Set(ref _exitDurationMs, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Exit easing")]
        [DisabledWhen(nameof(ExitAnimates), false)]
        public DrawKeystrokeEasing ExitEasing
        {
            get => _exitEasing;
            set => Set(ref _exitEasing, value);
        }

        /// <summary>Gates for the length and easing rows. [DisabledWhen] is bool-only, so these
        /// stand between it and the enums. Not [Browsable], so they are not rows of their own.</summary>
        [Browsable(false), JsonIgnore]
        public bool EntryAnimates => _entryKind != DrawKeystrokeAnimation.None;

        [Browsable(false), JsonIgnore]
        public bool ExitAnimates => _exitKind != DrawKeystrokeAnimation.None;

        // the keyboard track's defaults (KeyboardContent, and EditorSession.AddKeyboardTrack's
        // transform and row transitions), so a live overlay and a fresh track look alike
        private DrawOnScreenMode _mode = DrawOnScreenMode.On;
        private bool _showKeystrokes;
        private DrawKeystrokeRegion _region = DrawKeystrokeRegion.Bottom;
        private int _margin = 36;
        private double _width = 0.5;
        private double _opacity = 1.0;
        private DrawKeystrokeFilter _filter = DrawKeystrokeFilter.None;
        private int _fontSize = 40;
        private int _lingerMs = 1000;
        private int _pauseBreakMs = 1000;
        private Color _textColor = Color.FromUInt32(0xFFFFFFFF);
        private Color _fillColor = Color.FromUInt32(0x8C000000);
        private DrawKeystrokeAnimation _entryKind = DrawKeystrokeAnimation.SlideUp;
        private int _entryDurationMs = 300;
        private DrawKeystrokeEasing _entryEasing = DrawKeystrokeEasing.CubicInOut;
        private DrawKeystrokeAnimation _exitKind = DrawKeystrokeAnimation.Fade;
        private int _exitDurationMs = 300;
        private DrawKeystrokeEasing _exitEasing = DrawKeystrokeEasing.CubicInOut;
    }
}
