using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.Helpers;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// The property inspector's view of the session's primary selection: one flat set of bindable
    /// properties over whatever item is selected, plus the section-visibility flags the panel shows
    /// and hides its groups with.
    ///
    /// Items are POCOs that <see cref="EditorSession"/> <b>replaces</b> wholesale on undo/redo, so
    /// nothing here caches an <see cref="Item"/> reference: every read re-resolves
    /// <see cref="EditorSession.PrimarySelectedItem"/> by id, and every write goes back through the
    /// session rather than onto the model. Writes carry <c>origin: this</c> so the session's echo
    /// (<see cref="EditorSession.ProjectChanged"/>) can be ignored — a re-read mid-edit would fight
    /// the spinner the user is holding — while a change from anywhere else (the timeline, the
    /// gizmo, undo) re-reads everything. The <c>_syncing</c> guard is the other half of that: it
    /// stops a re-read from writing the values it just read straight back into the session.
    ///
    /// <b>What fans out over the row.</b> A recording row's items are the split segments of one
    /// continuous feed: where that picture sits on the canvas, and what it is masked or cropped to,
    /// are properties of the <i>feed</i>, not of a segment — so a transform/mask/crop edit on any
    /// segment writes every linked segment of the row in one <see cref="EditorSession.EditItems"/>
    /// call (one pipeline run, one undo entry — see <see cref="ItemRowScope"/>, which the preview
    /// gizmo shares so a spinner and a drag touch the same items), exactly as the webcam sidebar
    /// this replaces behaved. Everything that is about a segment's own edges or its own sound — entry/exit
    /// transitions and volume — stays single-item, because varying those per segment is precisely
    /// why the user split the row. An unlinked item (an import, a text card, an unlinked row) is a
    /// row of one either way, so both paths agree there.
    ///
    /// Deliberately free of Avalonia types: the panel binds to this, and the tests drive it with no
    /// UI framework at all.
    /// </summary>
    public sealed class SelectedItemViewModel : SimpleNotifyObject
    {
        /// <summary>What a newly created transition gets: long enough to read as an effect, short
        /// enough not to eat a short clip.</summary>
        public const double DefaultTransitionMs = 300;

        public const TransitionEasing DefaultTransitionEasing = TransitionEasing.CubicInOut;

        /// <summary>Corner radius (fraction of item height) offered the first time a rounded mask
        /// is chosen — the v1 webcam overlay's default.</summary>
        public const double DefaultCornerRadius = 0.25;

        /// <summary>The playback-speed picker's fixed menu. A class (not a bare double) so the
        /// dropdown's label can say "1.0 (normal)" while the value stays numeric; instances are
        /// singletons so reference equality works for list selection.</summary>
        public sealed class SpeedOption
        {
            public SpeedOption(double value, string label)
            {
                Value = value;
                Label = label;
            }

            public double Value { get; }

            public string Label { get; }

            public override string ToString() => Label;
        }

        public static readonly IReadOnlyList<SpeedOption> SpeedOptions = new[]
        {
            new SpeedOption(0.25, "0.25"),
            new SpeedOption(0.5, "0.5"),
            new SpeedOption(0.75, "0.75"),
            new SpeedOption(1.0, "1.0 (normal)"),
            new SpeedOption(1.25, "1.25"),
            new SpeedOption(1.5, "1.5"),
            new SpeedOption(1.75, "1.75"),
            new SpeedOption(2.0, "2"),
            new SpeedOption(3.0, "3"),
            new SpeedOption(4.0, "4"),
            new SpeedOption(10.0, "10"),
        };

        /// <summary>Normal playback — the Speed row's default, and what its reset dot writes back.
        /// The singleton from <see cref="SpeedOptions"/>, so reference equality holds.</summary>
        public static readonly SpeedOption DefaultSpeedOption =
            SpeedOptions.First(o => o.Value == 1.0);

        /// <summary>The speed <i>effect</i> item's target menu — its own list, not the per-clip
        /// one: this one re-times the whole output while an item is active, so it reaches further
        /// in both directions (and keeps 1.0 as the "hold, but change nothing" option).</summary>
        public static readonly IReadOnlyList<SpeedOption> SpeedTargetOptions = new[]
        {
            new SpeedOption(0.25, "0.25×"),
            new SpeedOption(0.5, "0.5×"),
            new SpeedOption(0.75, "0.75×"),
            new SpeedOption(1.0, "1.0× (no change)"),
            new SpeedOption(1.25, "1.25×"),
            new SpeedOption(1.5, "1.5×"),
            new SpeedOption(2.0, "2×"),
            new SpeedOption(3.0, "3×"),
            new SpeedOption(4.0, "4×"),
            new SpeedOption(5.0, "5×"),
            new SpeedOption(8.0, "8×"),
            new SpeedOption(10.0, "10×"),
        };

        /// <summary>What a new speed item is created at (<see cref="SpeedContent.Factor"/>'s own
        /// default), and what the target row's reset dot writes back.</summary>
        public static readonly SpeedOption DefaultSpeedTargetOption =
            SpeedTargetOptions.First(o => o.Value == 2.0);

        /// <summary>A string-valued picker entry: the model's wire value beside the label the menu
        /// shows. The same shape — and the same singleton discipline — as <see cref="SpeedOption"/>:
        /// the lists below are the only instances, so reference equality selects the row.</summary>
        public sealed class NamedOption
        {
            public NamedOption(string value, string label)
            {
                Value = value;
                Label = label;
            }

            public string Value { get; }

            public string Label { get; }

            public override string ToString() => Label;
        }

        /// <summary>Display names for the cursor styles the model offers. Built off
        /// <see cref="CursorContent.Styles"/> so the menu's order and membership are the model's;
        /// a style with no entry here shows its wire name rather than disappearing.</summary>
        private static readonly Dictionary<string, string> CursorStyleLabels = new Dictionary<string, string>
        {
            ["none"] = "None",
            ["native"] = "Native",
            ["vision"] = "Vision",
            ["point"] = "Point",
            ["bibata"] = "Bibata",
            ["breezex"] = "BreezeX",
            ["macos"] = "macOS",
            ["fuchsia"] = "Fuchsia",
            ["neon"] = "Neon",
        };

        private static readonly Dictionary<string, string> ClickAnimationLabels = new Dictionary<string, string>
        {
            ["none"] = "None",
            ["ripple"] = "Ripple",
            ["pulse"] = "Pulse",
            ["ring"] = "Ring",
            ["pressure"] = "Pressure",
        };

        public static readonly IReadOnlyList<NamedOption> CursorStyleOptions =
            BuildOptions(CursorContent.Styles, CursorStyleLabels);

        /// <summary>The colourway tiles for each style that has more than one, keyed by style —
        /// built once off <see cref="CursorAssets.Variants"/>, so a pack that declares its own
        /// colourways gets its own row of tiles without the picker knowing the pack exists. A style
        /// with one colourway has no entry and shows no second row (see
        /// <see cref="CursorVariantsVisible"/>).</summary>
        private static readonly Dictionary<string, IReadOnlyList<NamedOption>> CursorVariantOptionsByStyle
            = BuildVariantOptions();

        private static Dictionary<string, IReadOnlyList<NamedOption>> BuildVariantOptions()
        {
            var byStyle = new Dictionary<string, IReadOnlyList<NamedOption>>(StringComparer.OrdinalIgnoreCase);
            foreach (var style in CursorContent.Styles)
            {
                var variants = CursorAssets.Variants(style);
                if (variants.Count < 2)
                    continue;

                var options = new List<NamedOption>(variants.Count);
                foreach (var variant in variants)
                    options.Add(new NamedOption(variant.Id, variant.Label));
                byStyle[style] = options;
            }
            return byStyle;
        }

        /// <summary>What a new cursor item is created at (<see cref="CursorContent.Style"/>'s own
        /// default), and what the style row's reset dot writes back.</summary>
        public static readonly NamedOption DefaultCursorStyleOption =
            FindOption(CursorStyleOptions, "vision");

        public static readonly IReadOnlyList<NamedOption> ClickAnimationOptions =
            BuildOptions(CursorContent.ClickAnimations, ClickAnimationLabels);

        public static readonly NamedOption DefaultClickAnimationOption =
            FindOption(ClickAnimationOptions, "none");

        /// <summary>A keystroke-filter picker entry: the model's enum beside the label the menu
        /// shows — the same singleton discipline as <see cref="SpeedOption"/>, so reference
        /// equality selects the row.</summary>
        public sealed class KeystrokeFilterOption
        {
            public KeystrokeFilterOption(KeystrokeFilter value, string label)
            {
                Value = value;
                Label = label;
            }

            public KeystrokeFilter Value { get; }

            public string Label { get; }

            public override string ToString() => Label;
        }

        /// <summary>The keystroke overlay's filter menu, in menu order — everything, only the
        /// keys that draw as keycaps, or only shortcut chords.</summary>
        public static readonly IReadOnlyList<KeystrokeFilterOption> KeystrokeFilterOptions = new[]
        {
            new KeystrokeFilterOption(KeystrokeFilter.None, "None"),
            new KeystrokeFilterOption(KeystrokeFilter.Special, "Special keys"),
            new KeystrokeFilterOption(KeystrokeFilter.Shortcuts, "Shortcuts"),
        };

        /// <summary>What a new keystroke overlay shows (<see cref="KeyboardContent.Filter"/>'s own
        /// default), and what the filter row's reset dot writes back.</summary>
        public static readonly KeystrokeFilterOption DefaultKeystrokeFilterOption =
            KeystrokeFilterOptions[0];

        /// <summary>The one style that draws the recorded cursor sprites instead of a themed glyph —
        /// the glyph-only rows (colourways, SURROUND) mean nothing while it is picked, so they leave
        /// the panel entirely (see <see cref="CursorGlyphEnabled"/>); the size row stays, because the
        /// composer scales the sprite by it too.</summary>
        public const string NativeCursorStyle = "native";

        /// <summary>The style that hides the cursor outright — the glyph-only rows leave the panel
        /// while it is picked (see <see cref="CursorGlyphEnabled"/>); the PLACEMENT size row and the
        /// HIGHLIGHT section (a click's own subject) stay.</summary>
        public const string NoneCursorStyle = "none";

        /// <summary><see cref="CursorContent.ClickColor"/>'s own default, mirrored so the highlight
        /// previews have a colour before any cursor row is selected.</summary>
        public const uint DefaultCursorClickColor = 0xFFFF0000;

        /// <summary><see cref="DefaultCursorClickColor"/> as the colour well's hex — what its
        /// reset dot writes back.</summary>
        public const string DefaultCursorClickColorHex = "#FFFF0000";

        /// <summary>The animation that draws no highlight at all — the one value that empties the
        /// section below the picker.</summary>
        public const string NoClickAnimation = "none";

        /// <summary>The ring highlight's inner fill default, straight off the model — what the
        /// FILL row's reset dot writes back.</summary>
        public const double DefaultCursorFillOpacity = CursorContent.DefaultFillOpacity;

        /// <summary>The highlight dials' shared range and default, straight off the model so a
        /// spinner cannot offer a number the project would refuse.</summary>
        public const double MinHighlightFactor = CursorContent.MinHighlightFactor;

        public const double MaxHighlightFactor = CursorContent.MaxHighlightFactor;

        public const double DefaultHighlightFactor = 1.0;

        /// <summary>The glyph-size range the spinner offers. Narrower than the model's own
        /// validated 0.25–5: those are the bounds a project may hold, these are the ones worth
        /// dialling.</summary>
        public const double MinCursorSize = 0.5;

        public const double MaxCursorSize = 5.0;

        public const double DefaultCursorSize = 1.0;

        /// <summary>The keystroke overlay's own ranges — <see cref="KeyboardContent"/>'s validated
        /// bounds, repeated so the spinners cannot offer a number the project would refuse.</summary>
        public const double MinKeyboardFontSize = 8;

        public const double MaxKeyboardFontSize = 200;

        public const double DefaultKeyboardFontSize = KeyboardContent.DefaultFontSize;

        public const double MaxKeyboardMs = 10_000;

        public const double DefaultKeyboardLingerMs = 1000;

        public const double DefaultKeyboardPauseBreakMs = 1000;

        /// <summary><see cref="KeyboardContent.DefaultTextColor"/> and
        /// <see cref="KeyboardContent.DefaultBackgroundColor"/> as the hex the colour wells (and
        /// their reset dots, which are XAML strings) speak.</summary>
        public const string DefaultKeyboardTextColorHex = "#FFFFFFFF";

        public const string DefaultKeyboardBackColorHex = "#8C000000";

        /// <summary>The surround dials' range — the model's own bounds (<see cref="Surround"/>),
        /// repeated so a spinner cannot offer a number the project would refuse. Both dials are
        /// fractions of the item's drawn extent, so both are shown as percentages.</summary>
        public const double MaxSurroundSize = Surround.MaxSize;

        public const double MaxSurroundDistance = Surround.MaxDistance;

        public const double MinScale = 0.01;
        public const double MaxScale = 4.0;
        public const double MaxVolume = 2.0;
        public const double MaxTransitionMs = 10_000;

        /// <summary>Shortest ramp the length spinner offers. A ramp is switched on and off by its
        /// checkbox now, so a zero-length one would be a switched-on ramp that does nothing — "no
        /// ramp" keeps exactly one representation (a null transition).</summary>
        public const double MinRampMs = 10;

        /// <summary>The zoom effect's range and starting magnification — the model's own validated
        /// bounds (<see cref="ZoomContent"/>), repeated here so the spinner cannot offer a number
        /// the project would refuse.</summary>
        public const double MinZoom = 1.0;

        public const double MaxZoom = 5.0;

        public const double DefaultZoom = 1.5;

        /// <summary>Largest inset per side: a crop that reached 1 would leave nothing to draw.</summary>
        public const double MaxCropInset = 0.95;

        public const double MinFontSize = 1;
        public const double MaxFontSize = 2000;

        /// <summary>How close a derived ratio must be to a preset to light its tile — covers the
        /// rounding a crop/scale round-trip through the model introduces without ever matching a
        /// neighbouring preset (the closest pair, 4:3 and 3:2, differ by ~12%).</summary>
        private const double AspectMatchTolerance = 0.01;

        /// <summary>The aspect-ratio tiles, in display order. Ratios are width/height.</summary>
        private static readonly (AspectTile Tile, double Ratio)[] AspectPresets =
        {
            (AspectTile.R169, 16 / 9.0),
            (AspectTile.R11, 1.0),
            (AspectTile.R45, 4 / 5.0),
            (AspectTile.R32, 3 / 2.0),
            (AspectTile.R43, 4 / 3.0),
        };

        /// <summary>Which kind of item the EFFECT section is currently describing — the dials'
        /// defaults differ between the two (see <see cref="Surround.DefaultsFor"/>), and a
        /// cursor only offers the section while a themed glyph is drawn.</summary>
        private enum SurroundSubject
        {
            None,
            Picture,
            Cursor,
        }

        private enum AspectTile
        {
            /// <summary>The content's own ratio — the default, and the way back from any other
            /// tile (there is no reset dot; Original IS the reset).</summary>
            Original,
            R169,
            R11,
            R45,
            R32,
            R43,
            Custom,

            /// <summary>Free sizing: an explicit height with no ratio held, which is what gives
            /// the gizmo its edge handles. Sticky — dragging the box to a ratio that happens to
            /// match a preset must not steal the selection.</summary>
            Unlocked,
        }

        private EditorSession _session;

        /// <summary>True while <see cref="Sync"/> is pushing model values into the backing fields —
        /// the setters must not write those back into the session.</summary>
        private bool _syncing;

        /// <summary>The rounded-corner radius to restore when a mask is turned off and back on:
        /// dropping the mask drops the radius from the model, and losing the user's number to a
        /// shape flip is exactly what the old webcam pane's SyncShapeControls avoided.</summary>
        private double _rememberedCornerRadius = DefaultCornerRadius;

        private bool _hasSelection;
        private bool _showTransform;
        private bool _showScale;
        private bool _showRotation;
        private bool _showMask;
        private bool _showCrop;
        private bool _showText;
        private bool _showAudio;
        private bool _showTransitions;
        private bool _showTrackHidden;
        private bool _showTrackMuted;

        private string _subjectName;
        private string _subjectKind;

        private double _positionX = 0.5;
        private double _positionY = 0.5;
        private double _scale = 1.0;
        private double _scaleHeight = 1.0;
        private bool _hasScaleY;
        private double _rotation;
        private double _opacity = 1.0;

        private AspectTile _aspectTile = AspectTile.Original;
        private bool _aspectStretch;
        private double _customAspectW = 16;
        private double _customAspectH = 9;
        private bool _cropModeActive;

        private bool _maskSquare = true;
        private bool _maskCircle;
        private bool _maskRounded;
        private bool _maskSquircle;
        private double _cornerRadius = DefaultCornerRadius;

        private double _cropLeft;
        private double _cropTop;
        private double _cropRight;
        private double _cropBottom;

        private string _text;
        private string _fontFamily;
        private double _fontSize = 48;
        private string _textColorHex = "#FFFFFFFF";
        private TextAlign _textAlign = TextAlign.Center;

        private double _volume = 1.0;

        private TransitionKind _entryKind;
        private double _entryDurationMs = DefaultTransitionMs;
        private TransitionEasing _entryEasing = DefaultTransitionEasing;
        private TransitionKind _exitKind;
        private double _exitDurationMs = DefaultTransitionMs;
        private TransitionEasing _exitEasing = DefaultTransitionEasing;

        private bool _trackHidden;
        private bool _trackMuted;
        private bool _isLinked;
        private bool _canDesync;

        private bool _showSpeed;
        private double _speed = 1.0;

        private bool _showSpeedEffect;
        private bool _showZoomEffect;
        private bool _showCursorTrack;
        private bool _showKeyboardTrack;
        private bool _showRamp;
        private double _speedFactor = 2.0;
        private double _zoomFactor = DefaultZoom;
        private double _zoomFocusX = 0.5;
        private double _zoomFocusY = 0.5;

        private SurroundSubject _surroundSubject;
        private SurroundKind _surroundKind;
        private string _surroundColorHex = HexOfArgb(Surround.DefaultShadowColor);
        private double _surroundSize;
        private double _surroundDistance;

        private string _cursorStyle = "vision";

        /// <summary>Null until the user picks one: the style's own default colourway.</summary>
        private string _cursorVariant;
        private double _cursorSize = DefaultCursorSize;
        private bool _cursorDebounce = true;
        private string _cursorClickAnimation = NoClickAnimation;
        private uint _cursorClickColor = DefaultCursorClickColor;
        private string _cursorClickColorHex = DefaultCursorClickColorHex;
        private double _cursorFillOpacity = DefaultCursorFillOpacity;
        private double _cursorHoldSize = DefaultHighlightFactor;
        private double _cursorClickSize = DefaultHighlightFactor;
        private double _cursorAnimationSpeed = DefaultHighlightFactor;
        private string _cursorCapturePath;

        private double _keyboardFontSize = DefaultKeyboardFontSize;
        private double _keyboardLingerMs = DefaultKeyboardLingerMs;
        private double _keyboardPauseBreakMs = DefaultKeyboardPauseBreakMs;
        private KeystrokeFilter _keyboardFilter = KeystrokeFilter.None;
        private string _keyboardTextColorHex = DefaultKeyboardTextColorHex;
        private string _keyboardBackColorHex = DefaultKeyboardBackColorHex;

        private bool _rampEntryEnabled;
        private double _rampEntryMs = DefaultTransitionMs;
        private TransitionEasing _rampEntryEasing = DefaultTransitionEasing;
        private bool _rampExitEnabled;
        private double _rampExitMs = DefaultTransitionMs;
        private TransitionEasing _rampExitEasing = DefaultTransitionEasing;

        public SelectedItemViewModel()
        {
            CommandUnlink = new RelayCommand
            {
                Executed = _ => Unlink(),
                CanExecute = _ => CanDesync,
                Text = "Unlink row",
            };
        }

        /// <summary>The session this inspects. Assigning attaches to its selection/change events
        /// (and detaches from any previous one); null leaves the inspector empty.</summary>
        public EditorSession Session
        {
            get => _session;
            set
            {
                if (ReferenceEquals(_session, value))
                    return;

                if (_session != null)
                {
                    _session.SelectionChanged -= Session_SelectionChanged;
                    _session.ProjectChanged -= Session_ProjectChanged;
                    _session.ValidationFailed -= Session_ValidationFailed;
                }

                _session = value;

                if (_session != null)
                {
                    _session.SelectionChanged += Session_SelectionChanged;
                    _session.ProjectChanged += Session_ProjectChanged;
                    _session.ValidationFailed += Session_ValidationFailed;
                }

                Sync();
            }
        }

        /// <summary>Turns the row's sync (link group) off — the header toggle's inspector twin.</summary>
        public RelayCommand CommandUnlink { get; }

        // ------------------------------------------------------------------- section visibility

        public bool HasSelection => _hasSelection;

        /// <summary>Position/scale/rotation/opacity — anything the compositor draws.</summary>
        public bool ShowTransform => _showTransform;

        /// <summary>Shown for pictures only. Text sizes through <see cref="FontSize"/> alone — the
        /// gizmo's corner drag writes the font size for text too, so a text card never needs (or
        /// shows) a second size number.</summary>
        public bool ShowScale => _showScale;

        /// <summary>The Rotation row. Off for the keystroke overlay: the composer draws its
        /// pill block upright whatever <c>Transform.Rotation</c> says (the gizmo withholds its
        /// rotate handle for the same reason), so a spinner here would write a number nothing
        /// reads — and knock the gizmo's chrome out of line with the drawn block.</summary>
        public bool ShowRotation => _showRotation;

        /// <summary>The PLACEMENT scale row's label: "Size" while the height follows the content,
        /// "Width" once a stretch has given the item a height of its own (which adds the Height
        /// row below).</summary>
        public string ScaleLabel => _hasScaleY ? "Width" : "Size";

        /// <summary>The second size row, shown only while the item carries an explicit height
        /// (<c>Transform.ScaleY</c>) — the Stretch fit mode, or a free edge-handle resize.</summary>
        public bool ShowScaleHeight => _showScale && _hasScaleY;

        public bool ShowMask => _showMask;

        public bool ShowCrop => _showCrop;

        public bool ShowText => _showText;

        /// <summary>Volume — audio rows only; a video row's item carries no sound in this model
        /// (audio streams are their own items on their own rows).</summary>
        public bool ShowAudio => _showAudio;

        /// <summary>The kind-based entry/exit section (fade/slide/wipe) — pictures and text only.
        /// Audio and the effect items animate through <see cref="ShowRamp"/> instead, where the only
        /// question is how long the ramp is: there is nothing to slide.</summary>
        public bool ShowTransitions => _showTransitions;

        /// <summary>The kind-free entry/exit section: a switch, a length and an easing, shown for
        /// the items whose animation is a ramp of their own value — an audio item's volume, a speed
        /// item's factor, a zoom item's magnification.</summary>
        public bool ShowRamp => _showRamp;

        /// <summary>The speed effect item's target section.</summary>
        public bool ShowSpeedEffect => _showSpeedEffect;

        /// <summary>The zoom effect item's magnification + focal point section.</summary>
        public bool ShowZoomEffect => _showZoomEffect;

        /// <summary>The cursor overlay's style/size/click section.</summary>
        public bool ShowCursorTrack => _showCursorTrack;

        /// <summary>The keystroke overlay's type/timing section.</summary>
        public bool ShowKeyboardTrack => _showKeyboardTrack;

        public bool ShowTrackHidden => _showTrackHidden;

        public bool ShowTrackMuted => _showTrackMuted;

        public bool ShowCornerRadius => _maskRounded;

        public bool ShowEntryOptions => _entryKind != TransitionKind.None;

        public bool ShowExitOptions => _exitKind != TransitionKind.None;

        /// <summary>The selected item's row name — the inspector's "what am I editing" line.</summary>
        public string SubjectName => _subjectName;

        /// <summary>Human name for the selected content ("Video", "Audio", "Text", …).</summary>
        public string SubjectKind => _subjectKind;

        // --------------------------------------------------------------------------- transform

        /// <summary>Item centre X, 0-1 of the canvas width (the panel shows it as a percentage).</summary>
        public double PositionX
        {
            get => _positionX;
            set
            {
                value = Clamp(value, 0, 1);
                if (!Set(ref _positionX, value) || _syncing)
                    return;

                EditRow("sel:x", i => TransformOf(i).X = value);
            }
        }

        public double PositionY
        {
            get => _positionY;
            set
            {
                value = Clamp(value, 0, 1);
                if (!Set(ref _positionY, value) || _syncing)
                    return;

                EditRow("sel:y", i => TransformOf(i).Y = value);
            }
        }

        /// <summary>Item width as a fraction of the canvas width.</summary>
        public double Scale
        {
            get => _scale;
            set
            {
                value = Clamp(value, MinScale, MaxScale);
                if (!Set(ref _scale, value) || _syncing)
                    return;

                EditRow("sel:scale", i => TransformOf(i).Scale = value);
            }
        }

        /// <summary>Item height as a fraction of the canvas height. Only meaningful — and only
        /// shown — while the item already carries an explicit height; an item whose height follows
        /// the content stores no <c>ScaleY</c> at all and this field is inert.</summary>
        public double ScaleHeight
        {
            get => _scaleHeight;
            set
            {
                value = Clamp(value, MinScale, MaxScale);
                if (!Set(ref _scaleHeight, value) || _syncing || !_hasScaleY)
                    return;

                EditRow("sel:scaleY", i => TransformOf(i).ScaleY = value);
            }
        }

        /// <summary>Clockwise rotation in degrees.</summary>
        public double Rotation
        {
            get => _rotation;
            set
            {
                value = Clamp(value, -360, 360);
                if (!Set(ref _rotation, value) || _syncing)
                    return;

                EditRow("sel:rotation", i => TransformOf(i).Rotation = value);
            }
        }

        public double Opacity
        {
            get => _opacity;
            set
            {
                value = Clamp(value, 0, 1);
                if (!Set(ref _opacity, value) || _syncing)
                    return;

                EditRow("sel:opacity", i => TransformOf(i).Opacity = value);
            }
        }

        // -------------------------------------------------------------------------------- mask

        /// <summary>The unmasked item: a square-cornered rectangle is what no mask already draws,
        /// so this writes <c>Mask = null</c> rather than a shape.</summary>
        public bool MaskSquare
        {
            get => _maskSquare;
            set
            {
                // radio group: the others it deselects report false, and only the selected one is
                // an edit — otherwise every flip would write the model twice.
                if (!Set(ref _maskSquare, value) || _syncing || !value)
                    return;

                SetMaskFlags(square: true, circle: false, rounded: false, squircle: false);
                EditRow("sel:mask", i => TransformOf(i).Mask = null);
            }
        }

        public bool MaskCircle
        {
            get => _maskCircle;
            set
            {
                if (!Set(ref _maskCircle, value) || _syncing || !value)
                    return;

                SetMaskFlags(square: false, circle: true, rounded: false, squircle: false);
                ApplyMaskShape(MaskShape.Circle);
            }
        }

        public bool MaskRounded
        {
            get => _maskRounded;
            set
            {
                if (!Set(ref _maskRounded, value) || _syncing || !value)
                    return;

                SetMaskFlags(square: false, circle: false, rounded: true, squircle: false);
                ApplyMaskShape(MaskShape.RoundedRect);
            }
        }

        public bool MaskSquircle
        {
            get => _maskSquircle;
            set
            {
                if (!Set(ref _maskSquircle, value) || _syncing || !value)
                    return;

                SetMaskFlags(square: false, circle: false, rounded: false, squircle: true);
                ApplyMaskShape(MaskShape.Squircle);
            }
        }

        /// <summary>Rounded-corner radius as a fraction of the item's height (0-0.5).</summary>
        public double CornerRadius
        {
            get => _cornerRadius;
            set
            {
                value = Clamp(value, 0, 0.5);
                if (!Set(ref _cornerRadius, value) || _syncing)
                    return;

                _rememberedCornerRadius = value;
                EditRow("sel:corner", i =>
                {
                    var mask = TransformOf(i).Mask;
                    if (mask != null)
                        mask.CornerRadius = value;
                });
            }
        }

        // ------------------------------------------------------------------ aspect ratio + crop

        /// <summary>The tile bools the aspect grid's radio buttons bind. Selecting one applies the
        /// ratio (via crop or stretch — see <see cref="ApplyAspect"/>); deselection is what a radio
        /// group does to the losers and writes nothing, exactly like the mask tiles.</summary>
        public bool AspectOriginal { get => _aspectTile == AspectTile.Original; set => SetAspectTile(AspectTile.Original, value); }

        public bool AspectUnlocked { get => _aspectTile == AspectTile.Unlocked; set => SetAspectTile(AspectTile.Unlocked, value); }

        public bool Aspect169 { get => _aspectTile == AspectTile.R169; set => SetAspectTile(AspectTile.R169, value); }

        public bool Aspect11 { get => _aspectTile == AspectTile.R11; set => SetAspectTile(AspectTile.R11, value); }

        public bool Aspect45 { get => _aspectTile == AspectTile.R45; set => SetAspectTile(AspectTile.R45, value); }

        public bool Aspect32 { get => _aspectTile == AspectTile.R32; set => SetAspectTile(AspectTile.R32, value); }

        public bool Aspect43 { get => _aspectTile == AspectTile.R43; set => SetAspectTile(AspectTile.R43, value); }

        public bool AspectCustom { get => _aspectTile == AspectTile.Custom; set => SetAspectTile(AspectTile.Custom, value); }

        /// <summary>The Custom ratio row, shown only while the Custom tile is selected.</summary>
        public bool ShowCustomAspect => _aspectTile == AspectTile.Custom;

        /// <summary>Whether a ratio-bearing tile (a preset or Custom) is selected — the fit-mode
        /// buttons are inert (and disabled) for Original and Unlocked, which hold no ratio.</summary>
        public bool AspectSelected => _aspectTile is not (AspectTile.Original or AspectTile.Unlocked);

        /// <summary>Fill keeps the picture's own pixels square and crops the excess (the default);
        /// its partner <see cref="AspectStretch"/> distorts instead. A radio pair.</summary>
        public bool AspectFill
        {
            get => !_aspectStretch;
            set => SetFitMode(stretch: false, value);
        }

        /// <summary>Stretch fits the target box exactly by distorting the picture — no pixels are
        /// cropped away, circles become ellipses.</summary>
        public bool AspectStretch
        {
            get => _aspectStretch;
            set => SetFitMode(stretch: true, value);
        }

        private void SetFitMode(bool stretch, bool selected)
        {
            if (!selected || _aspectStretch == stretch)
                return; // radio deselection, or no change

            _aspectStretch = stretch;
            OnPropertyChanged(nameof(AspectFill));
            OnPropertyChanged(nameof(AspectStretch));

            // reapply only when a ratio is in force — flipping the mode on Original/Unlocked must
            // not clear a hand-made crop or the free height.
            if (!_syncing && AspectSelected)
                ApplyAspect();
        }

        /// <summary>Custom ratio numerator (width part of W:H).</summary>
        public double CustomAspectW
        {
            get => _customAspectW;
            set
            {
                value = Clamp(value, 0.1, 1000);
                if (!Set(ref _customAspectW, value) || _syncing)
                    return;

                if (_aspectTile == AspectTile.Custom)
                    ApplyAspect();
            }
        }

        /// <summary>Custom ratio denominator (height part of W:H).</summary>
        public double CustomAspectH
        {
            get => _customAspectH;
            set
            {
                value = Clamp(value, 0.1, 1000);
                if (!Set(ref _customAspectH, value) || _syncing)
                    return;

                if (_aspectTile == AspectTile.Custom)
                    ApplyAspect();
            }
        }

        /// <summary>Fractional inset cropped off the picture's left edge (0-0.95). Written per
        /// row like every placement edit; all four back at zero collapses the crop to null.</summary>
        public double CropLeft
        {
            get => _cropLeft;
            set
            {
                value = Clamp(value, 0, MaxCropInset);
                if (!Set(ref _cropLeft, value) || _syncing)
                    return;

                OnPropertyChanged(nameof(CropTotal));
                EditRow("sel:cropl", i => SetCrop(i, c => c.Left = value));
            }
        }

        public double CropTop
        {
            get => _cropTop;
            set
            {
                value = Clamp(value, 0, MaxCropInset);
                if (!Set(ref _cropTop, value) || _syncing)
                    return;

                OnPropertyChanged(nameof(CropTotal));
                EditRow("sel:cropt", i => SetCrop(i, c => c.Top = value));
            }
        }

        public double CropRight
        {
            get => _cropRight;
            set
            {
                value = Clamp(value, 0, MaxCropInset);
                if (!Set(ref _cropRight, value) || _syncing)
                    return;

                OnPropertyChanged(nameof(CropTotal));
                EditRow("sel:cropr", i => SetCrop(i, c => c.Right = value));
            }
        }

        public double CropBottom
        {
            get => _cropBottom;
            set
            {
                value = Clamp(value, 0, MaxCropInset);
                if (!Set(ref _cropBottom, value) || _syncing)
                    return;

                OnPropertyChanged(nameof(CropTotal));
                EditRow("sel:cropb", i => SetCrop(i, c => c.Bottom = value));
            }
        }

        /// <summary>Sum of the four crop insets: 0 means "no crop", anything else lights the crop
        /// row's reset dot. Writing 0 removes the crop entirely (the dot's reset click).</summary>
        public double CropTotal
        {
            get => _cropLeft + _cropTop + _cropRight + _cropBottom;
            set
            {
                if (_syncing || value != 0 || CropTotal == 0)
                    return;

                EditRow("sel:cropclear", i => TransformOf(i).Crop = null, origin: null);
            }
        }

        /// <summary>True while the preview's gizmo is in crop mode (dragging the item's edges
        /// adjusts the crop instead of the size). Pure UI state — nothing in the project changes
        /// until a handle is dragged — cleared whenever the selection changes.</summary>
        public bool CropModeActive
        {
            get => _cropModeActive;
            set => Set(ref _cropModeActive, value);
        }

        /// <summary>Tile setter body: mask-tile semantics (only a true write applies).</summary>
        private void SetAspectTile(AspectTile tile, bool selected)
        {
            if (!selected)
            {
                // a radio group deselecting the loser; the winner's own set does the work
                if (_aspectTile == tile && !_syncing)
                    OnPropertyChanged(TileProperty(tile));
                return;
            }

            if (_aspectTile == tile)
                return;

            SetAspectFlags(tile);

            if (!_syncing)
                ApplyAspect();
        }

        /// <summary>Moves the selection to <paramref name="tile"/>, raising change notifications
        /// for every tile property that flipped plus the dependents.</summary>
        private void SetAspectFlags(AspectTile tile)
        {
            var previous = _aspectTile;
            _aspectTile = tile;

            foreach (var name in new[] { previous, tile }.Distinct().Select(TileProperty))
            {
                if (name != null)
                    OnPropertyChanged(name);
            }

            OnPropertyChanged(nameof(ShowCustomAspect));
            OnPropertyChanged(nameof(AspectSelected));
        }

        private static string TileProperty(AspectTile tile) => tile switch
        {
            AspectTile.Original => nameof(AspectOriginal),
            AspectTile.R169 => nameof(Aspect169),
            AspectTile.R11 => nameof(Aspect11),
            AspectTile.R45 => nameof(Aspect45),
            AspectTile.R32 => nameof(Aspect32),
            AspectTile.R43 => nameof(Aspect43),
            AspectTile.Custom => nameof(AspectCustom),
            AspectTile.Unlocked => nameof(AspectUnlocked),
            _ => null,
        };

        /// <summary>The ratio (width/height) the current tile stands for, or null for the two
        /// ratio-free tiles (Original and Unlocked).</summary>
        private double? SelectedAspectRatio()
        {
            if (_aspectTile == AspectTile.Custom)
                return _customAspectH > 0 ? _customAspectW / _customAspectH : null;

            foreach (var (tile, ratio) in AspectPresets)
            {
                if (tile == _aspectTile)
                    return ratio;
            }

            return null;
        }

        /// <summary>
        /// Writes the selected tile + fit mode into the model — always onto
        /// <see cref="Transform.Aspect"/>/<see cref="Transform.AspectStretch"/>, never the crop:
        /// the composer resolves the ratio itself (see <c>AspectMath</c>), and the crop fields stay
        /// whatever the user cut, applied after the ratio. Unlocked instead trades the ratio for an
        /// explicit height; Original clears everything ratio-shaped and nothing else.
        /// </summary>
        private void ApplyAspect()
        {
            if (_aspectTile == AspectTile.Unlocked)
            {
                // an explicit height seeded from what the item is drawn at right now, so the
                // picture does not move — the point is the free edge handles, not a jump. Read
                // BEFORE the aspect is cleared: the seed is the drawn (aspect-shaped) height.
                // Per item, not per row: linked segments share the transform's numbers but each
                // resolves its own content aspect.
                EditRow("sel:aspect", i =>
                {
                    var height = CurrentHeightFraction(i);
                    var t = TransformOf(i);
                    t.ScaleY = height;
                    t.Aspect = null;
                    t.AspectStretch = false;
                }, origin: null);
                return;
            }

            var ratio = SelectedAspectRatio();
            var stretch = _aspectStretch;
            EditRow("sel:aspect", i =>
            {
                var t = TransformOf(i);
                t.ScaleY = null;
                t.Aspect = ratio is > 0 ? ratio : null;
                t.AspectStretch = ratio is > 0 && stretch;
            }, origin: null);
        }

        /// <summary>The height an item is drawn at now, as the canvas fraction <c>ScaleY</c> stores
        /// — so switching to Unlocked is a no-op on the picture. Null when the content's aspect
        /// cannot be resolved, which leaves the item deriving its height (the composer's default)
        /// rather than snapping it to a guess.</summary>
        private double? CurrentHeightFraction(Item item)
        {
            var transform = item?.Transform;
            if (transform == null)
                return null;

            // text scales off its own natural block, so its two axes already share one unit
            if (item.Content is TextContent)
                return transform.Scale;

            var output = _session?.Project?.Output;
            double canvasW = output?.WidthPx ?? 0;
            double canvasH = output?.HeightPx ?? 0;
            if (!(canvasW > 0) || !(canvasH > 0))
                return null;

            var aspect = ItemPlacement.ContentAspect(_session.Project, item, canvasW, canvasH);
            if (aspect is not > 0)
                return null;

            return transform.Scale * canvasW * aspect.Value / canvasH;
        }

        /// <summary>
        /// Reads the tile selection straight out of the model: an explicit height is Unlocked
        /// (free sizing, whatever ratio it happens to be at), a stored <see cref="Transform.Aspect"/>
        /// selects its preset — or Custom, which is <b>sticky</b> when its ratio equals a preset's
        /// (the user's tile choice is honoured, not re-guessed) — and nothing means Original.
        /// A hand-made crop never lights a tile: the crop is the user's, applied after the ratio.
        /// </summary>
        private void SyncAspect(Item item)
        {
            var tile = AspectTile.Original;
            var stretch = _aspectStretch;

            var transform = item?.Transform;
            if (transform?.ScaleY != null)
            {
                tile = AspectTile.Unlocked;
            }
            else if (transform?.Aspect is > 0 and var ratio)
            {
                stretch = transform.AspectStretch;
                tile = MatchAspectTile(ratio, fallback: AspectTile.Custom);

                // a stored ratio the Custom spinners do not currently show (a reloaded project,
                // say): make the Custom row tell the truth about what is applied
                if (tile == AspectTile.Custom && !CustomMatches(ratio))
                {
                    Set(ref _customAspectW, Math.Round(ratio, 2), nameof(CustomAspectW));
                    Set(ref _customAspectH, 1, nameof(CustomAspectH));
                }
            }
            else
            {
                stretch = false;
            }

            SetAspectFlags(tile);

            if (_aspectStretch != stretch)
            {
                _aspectStretch = stretch;
                OnPropertyChanged(nameof(AspectFill));
                OnPropertyChanged(nameof(AspectStretch));
            }
        }

        /// <summary>Custom-first when Custom is the current tile (stickiness), then the presets,
        /// then Custom by value, then the fallback for ratios matching nothing.</summary>
        private AspectTile MatchAspectTile(double ratio, AspectTile fallback)
        {
            if (!(ratio > 0))
                return fallback;

            if (_aspectTile == AspectTile.Custom && CustomMatches(ratio))
                return AspectTile.Custom;

            foreach (var (tile, preset) in AspectPresets)
            {
                if (Math.Abs(ratio - preset) / preset < AspectMatchTolerance)
                    return tile;
            }

            return CustomMatches(ratio) ? AspectTile.Custom : fallback;
        }

        private bool CustomMatches(double ratio)
        {
            if (_customAspectH <= 0)
                return false;

            var custom = _customAspectW / _customAspectH;
            return custom > 0 && Math.Abs(ratio - custom) / custom < AspectMatchTolerance;
        }

        // -------------------------------------------------------------------------------- text

        public string Text
        {
            get => _text;
            set
            {
                if (!Set(ref _text, value) || _syncing)
                    return;

                EditText("sel:text", t => t.Text = value);
            }
        }

        /// <summary>Font family name; empty means the renderer's default face.</summary>
        public string FontFamily
        {
            get => _fontFamily;
            set
            {
                if (!Set(ref _fontFamily, value) || _syncing)
                    return;

                var font = String.IsNullOrWhiteSpace(value) ? null : value.Trim();
                EditText("sel:font", t => t.Font = font);
            }
        }

        /// <summary>Font size in output-canvas pixels.</summary>
        public double FontSize
        {
            get => _fontSize;
            set
            {
                value = Clamp(value, MinFontSize, MaxFontSize);
                if (!Set(ref _fontSize, value) || _syncing)
                    return;

                EditText("sel:fontsize", t => t.Size = value);
            }
        }

        /// <summary>Text colour as <c>#RRGGBB</c> or <c>#AARRGGBB</c>. A half-typed value is kept
        /// in the box but not written to the model — the next re-read puts the model's value back.</summary>
        public string TextColorHex
        {
            get => _textColorHex;
            set
            {
                if (!Set(ref _textColorHex, value) || _syncing)
                    return;

                if (!IsHexColor(value))
                    return;

                var color = value.Trim();
                EditText("sel:textcolor", t => t.Color = color);
            }
        }

        public TextAlign TextAlign
        {
            get => _textAlign;
            set
            {
                if (!Set(ref _textAlign, value) || _syncing)
                    return;

                EditText("sel:align", t => t.Align = value);
            }
        }

        // ------------------------------------------------------------------------------- audio

        /// <summary>Linear gain, 1 = unity. Per item: a segment quietened on purpose must stay
        /// quieter than its neighbours.</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                value = Clamp(value, 0, MaxVolume);
                if (!Set(ref _volume, value) || _syncing)
                    return;

                EditSelected("sel:volume", i => i.Volume = value);
            }
        }

        // ------------------------------------------------------------------------- transitions

        public TransitionKind EntryKind
        {
            get => _entryKind;
            set
            {
                if (!Set(ref _entryKind, value) || _syncing)
                    return;

                OnPropertyChanged(nameof(ShowEntryOptions));
                ApplyTransition(entry: true, value);
            }
        }

        public double EntryDurationMs
        {
            get => _entryDurationMs;
            set
            {
                value = Clamp(value, 0, MaxTransitionMs);
                if (!Set(ref _entryDurationMs, value) || _syncing)
                    return;

                var ticks = MsToTicks(value);
                EditSelected("sel:entryms", i =>
                {
                    if (i.Entry != null)
                        i.Entry.DurationTicks = ticks;
                });
            }
        }

        public TransitionEasing EntryEasing
        {
            get => _entryEasing;
            set
            {
                if (!Set(ref _entryEasing, value) || _syncing)
                    return;

                EditSelected("sel:entryease", i =>
                {
                    if (i.Entry != null)
                        i.Entry.Easing = value;
                });
            }
        }

        public TransitionKind ExitKind
        {
            get => _exitKind;
            set
            {
                if (!Set(ref _exitKind, value) || _syncing)
                    return;

                OnPropertyChanged(nameof(ShowExitOptions));
                ApplyTransition(entry: false, value);
            }
        }

        public double ExitDurationMs
        {
            get => _exitDurationMs;
            set
            {
                value = Clamp(value, 0, MaxTransitionMs);
                if (!Set(ref _exitDurationMs, value) || _syncing)
                    return;

                var ticks = MsToTicks(value);
                EditSelected("sel:exitms", i =>
                {
                    if (i.Exit != null)
                        i.Exit.DurationTicks = ticks;
                });
            }
        }

        public TransitionEasing ExitEasing
        {
            get => _exitEasing;
            set
            {
                if (!Set(ref _exitEasing, value) || _syncing)
                    return;

                EditSelected("sel:exitease", i =>
                {
                    if (i.Exit != null)
                        i.Exit.Easing = value;
                });
            }
        }

        // ------------------------------------------------------------------------------- track

        /// <summary>The row's enable toggle (video). Mirrors <see cref="Track.Hidden"/>.</summary>
        public bool TrackHidden
        {
            get => _trackHidden;
            set
            {
                if (!Set(ref _trackHidden, value) || _syncing)
                    return;

                var track = SelectedTrack;
                if (track != null && track.Hidden != value)
                    _session.SetTrackHidden(track.Id, value, this);
            }
        }

        /// <summary>The row's enable toggle (audio). Mirrors <see cref="Track.Muted"/>.</summary>
        public bool TrackMuted
        {
            get => _trackMuted;
            set
            {
                if (!Set(ref _trackMuted, value) || _syncing)
                    return;

                var track = SelectedTrack;
                if (track != null && track.Muted != value)
                    _session.SetTrackMuted(track.Id, value, this);
            }
        }

        /// <summary>True when the selected item still moves with the rest of its recording.</summary>
        public bool IsLinked => _isLinked;

        /// <summary>Whether the synced-object banner offers the Desync button. The cursor and
        /// keystroke overlays only make sense against the recording their input data came from,
        /// so they can never be cut loose — the banner explains the lock but offers no way out.</summary>
        public bool CanDesync => _canDesync;

        /// <summary>The Speed row: media items (video or audio) only, and only once the row is
        /// desynced — a linked segment keeps the recording's own clock, so re-timing it is not
        /// offered until the user cuts it loose.</summary>
        public bool ShowSpeed => _showSpeed;

        /// <summary>The playback-speed dropdown's selection, as one of <see cref="SpeedOptions"/>.
        /// Writing goes through <see cref="EditorSession.SetItemSpeed"/>, which re-times the item
        /// on the timeline (duration scales with the speed) — origin null, because the write moves
        /// state the setters do not mirror (the item's duration), so the inspector wants its own
        /// re-read, and the timeline needs the change event either way.</summary>
        public SpeedOption SpeedChoice
        {
            get
            {
                SpeedOption best = null;
                foreach (var option in SpeedOptions)
                {
                    if (best == null || Math.Abs(option.Value - _speed) < Math.Abs(best.Value - _speed))
                        best = option;
                }
                return best;
            }
            set
            {
                if (value == null || _syncing)
                    return;

                var item = SelectedItem;
                if (item == null || value.Value == _speed)
                    return;

                _speed = value.Value;
                OnPropertyChanged(nameof(SpeedChoice));
                _session.SetItemSpeed(item.Id, value.Value, origin: null);
            }
        }

        // ------------------------------------------------------------------------ effect items

        /// <summary>The speed item's target factor, as one of <see cref="SpeedTargetOptions"/>.
        /// Unlike <see cref="SpeedChoice"/> this leaves the item where it is — the factor re-times
        /// the output, not the item's span — so nothing the setters do not mirror moves and the
        /// write carries <c>origin: this</c>.</summary>
        public SpeedOption SpeedTarget
        {
            get
            {
                SpeedOption best = null;
                foreach (var option in SpeedTargetOptions)
                {
                    if (best == null || Math.Abs(option.Value - _speedFactor) < Math.Abs(best.Value - _speedFactor))
                        best = option;
                }
                return best;
            }
            set
            {
                if (value == null || _syncing)
                    return;

                var item = SelectedItem;
                if (item?.Content is not SpeedContent || value.Value == _speedFactor)
                    return;

                _speedFactor = value.Value;
                OnPropertyChanged(nameof(SpeedTarget));
                _session.SetSpeedFactor(item.Id, value.Value, this);
            }
        }

        /// <summary>The zoom item's magnification (1 = untouched). Single-item like every other
        /// property of an effect item: effect items are never linked into a row.</summary>
        public double ZoomFactor
        {
            get => _zoomFactor;
            set
            {
                value = Clamp(value, MinZoom, MaxZoom);
                if (!Set(ref _zoomFactor, value) || _syncing)
                    return;

                EditZoom("sel:zoom", z => z.Zoom = value);
            }
        }

        /// <summary>Focal point X, 0-1 of the canvas width — the preview's crosshair writes the
        /// same field from the other end.</summary>
        public double ZoomFocusX
        {
            get => _zoomFocusX;
            set
            {
                value = Clamp(value, 0, 1);
                if (!Set(ref _zoomFocusX, value) || _syncing)
                    return;

                EditZoom("sel:zoomx", z => z.FocusX = value);
            }
        }

        public double ZoomFocusY
        {
            get => _zoomFocusY;
            set
            {
                value = Clamp(value, 0, 1);
                if (!Set(ref _zoomFocusY, value) || _syncing)
                    return;

                EditZoom("sel:zoomy", z => z.FocusY = value);
            }
        }

        // ---------------------------------------------------------------------- input overlays

        /// <summary>
        /// The cursor overlay's style, as one of <see cref="CursorStyleOptions"/>. Every property
        /// of these two sections writes the whole row (<see cref="EditCursor"/> /
        /// <see cref="EditKeyboard"/> fan out like <see cref="EditRow"/>): the segments of an
        /// overlay row are the split pieces of one continuous overlay, so a style the user picked
        /// on one piece belongs to all of them — an unlinked row is a row of one either way.
        /// </summary>
        public NamedOption CursorStyle
        {
            get => FindOption(CursorStyleOptions, _cursorStyle);
            set
            {
                if (value == null || _syncing || value.Value == _cursorStyle)
                    return;

                _cursorStyle = value.Value;
                OnPropertyChanged(nameof(CursorStyle));
                OnPropertyChanged(nameof(CursorGlyphEnabled));
                // the colourway row belongs to the style above it: which tiles it offers, whether
                // it is there at all, and which of them reads as picked all change with the style
                OnPropertyChanged(nameof(CursorVariantOptions));
                OnPropertyChanged(nameof(CursorVariantsVisible));
                OnPropertyChanged(nameof(CursorVariant));
                // the SURROUND section decorates the themed glyph; the native style has none
                OnPropertyChanged(nameof(ShowSurround));
                EditCursor("sel:cursorstyle", c => c.Style = value.Value);
            }
        }

        /// <summary>The colourway tiles the picked style offers — empty for a style with only one,
        /// which is when the row is not shown at all.</summary>
        public IReadOnlyList<NamedOption> CursorVariantOptions =>
            CursorVariantOptionsByStyle.TryGetValue(_cursorStyle ?? "", out var options)
                ? options
                : Array.Empty<NamedOption>();

        /// <summary>Whether the colourway row is on show: only for a themed style that has more
        /// than one palette to choose between.</summary>
        public bool CursorVariantsVisible => CursorGlyphEnabled && CursorVariantOptions.Count > 0;

        /// <summary>
        /// Which of the style's colourways is drawn. The stored value is deliberately left alone
        /// when the style changes — a project remembers "light" across a trip through another style
        /// — and the getter resolves whatever is stored against the style actually picked, so a
        /// colourway that style does not offer reads as its default exactly as the compositor
        /// draws it.
        /// </summary>
        public NamedOption CursorVariant
        {
            get => FindOption(CursorVariantOptions,
                CursorAssets.ResolveVariant(_cursorStyle, _cursorVariant));
            set
            {
                if (value == null || _syncing || value.Value == _cursorVariant)
                    return;

                _cursorVariant = value.Value;
                OnPropertyChanged(nameof(CursorVariant));
                EditCursor("sel:cursorvariant", c => c.Variant = value.Value);
            }
        }

        /// <summary>The capture sidecar of the recording the selected cursor overlay is synced to
        /// (<see cref="Source.InputCapturePath"/>), or null when the item is not a cursor overlay or
        /// its source is gone. Read-only, and here only for the <c>native</c> style tile: that style
        /// draws the sprites the recorder rasterized into this file, so its preview has to read
        /// them out of it.</summary>
        public string CursorCapturePath => _cursorCapturePath;

        /// <summary>Whether the glyph-only rows (the colourway tiles, the whole SURROUND section)
        /// mean anything — they are hidden, not merely greyed, when they do not: <c>native</c>
        /// draws the recorded sprite, which carries its own palette and the system cursor's own
        /// shadow, and <c>none</c> draws nothing at all, so a disabled control would only invite
        /// the question of what it would have done.</summary>
        public bool CursorGlyphEnabled => _cursorStyle != NativeCursorStyle && _cursorStyle != NoneCursorStyle;

        /// <summary>Glyph size multiplier over the style's base size.</summary>
        public double CursorSize
        {
            get => _cursorSize;
            set
            {
                value = Clamp(value, MinCursorSize, MaxCursorSize);
                if (!Set(ref _cursorSize, value) || _syncing)
                    return;

                EditCursor("sel:cursorsize", c => c.Size = value);
            }
        }

        /// <summary>Debounces the capture's typing-hidden flicker (see
        /// <see cref="CursorContent.Debounce"/>): on keeps a hidden cursor hidden until it moves
        /// or clicks again; off draws exactly what the recorder sampled.</summary>
        public bool CursorDebounce
        {
            get => _cursorDebounce;
            set
            {
                if (!Set(ref _cursorDebounce, value) || _syncing)
                    return;

                EditCursor("sel:cursordebounce", c => c.Debounce = value);
            }
        }

        /// <summary>What a mouse click draws, as one of <see cref="ClickAnimationOptions"/>.</summary>
        public NamedOption CursorClickAnimation
        {
            get => FindOption(ClickAnimationOptions, _cursorClickAnimation);
            set
            {
                if (value == null || _syncing || value.Value == _cursorClickAnimation)
                    return;

                _cursorClickAnimation = value.Value;
                OnPropertyChanged(nameof(CursorClickAnimation));
                RaiseHighlightRowFlags();
                EditCursor("sel:cursorclick", c => c.ClickAnimation = value.Value);
            }
        }

        /// <summary>Whether the highlight's dials mean anything: <c>none</c> draws no
        /// highlight, so its size and speed rows leave the panel rather than sit there inert — the
        /// same trade the glyph rows make under the native style.</summary>
        public bool CursorHighlightEnabled => _cursorClickAnimation != NoClickAnimation;

        /// <summary>Whether the colour well means anything — every drawn highlight except
        /// <c>pressure</c>, which colours nothing (it warps the pixels beneath).</summary>
        public bool CursorHighlightColorEnabled =>
            CursorHighlightEnabled && ClickHighlight.ModeOf(_cursorClickAnimation) != HighlightMode.Press;

        /// <summary>Whether the hold-size dial means anything — only the burst animations draw
        /// the held dot it scales; ring and press animate the hold themselves.</summary>
        public bool CursorHoldSizeEnabled =>
            ClickHighlight.ModeOf(_cursorClickAnimation) is HighlightMode.Ripple or HighlightMode.Pulse;

        /// <summary>Whether the FILL row shows — the ring is the one highlight with an inner
        /// fill of its own.</summary>
        public bool CursorRingFillEnabled =>
            ClickHighlight.ModeOf(_cursorClickAnimation) == HighlightMode.Ring;

        private void RaiseHighlightRowFlags()
        {
            OnPropertyChanged(nameof(CursorHighlightEnabled));
            OnPropertyChanged(nameof(CursorHighlightColorEnabled));
            OnPropertyChanged(nameof(CursorHoldSizeEnabled));
            OnPropertyChanged(nameof(CursorRingFillEnabled));
        }

        /// <summary>The highlight colour (packed ARGB) the composer draws clicks in — the parsed
        /// twin of <see cref="CursorClickColorHex"/>, kept because the highlight preview tiles
        /// bind a packed colour, not a hex string.</summary>
        public uint CursorClickColor => _cursorClickColor;

        /// <summary>The highlight colour as <c>#RRGGBB</c> or <c>#AARRGGBB</c> — the colour well's
        /// face of <see cref="CursorClickColor"/>. Half-typed values stay in the box unwritten,
        /// exactly as <see cref="KeyboardTextColorHex"/> does.</summary>
        public string CursorClickColorHex
        {
            get => _cursorClickColorHex;
            set
            {
                if (!Set(ref _cursorClickColorHex, value) || _syncing)
                    return;

                if (!TryParseArgb(value, out var argb))
                    return;

                Set(ref _cursorClickColor, argb, nameof(CursorClickColor));
                EditCursor("sel:cursorclickcolor", c => c.ClickColor = argb);
            }
        }

        /// <summary>The ring highlight's inner fill opacity, 0..1 — the outline stays at the
        /// colour's own alpha, this dials only the disc inside it.</summary>
        public double CursorFillOpacity
        {
            get => _cursorFillOpacity;
            set
            {
                value = Clamp(value, 0.0, 1.0);
                if (!Set(ref _cursorFillOpacity, value) || _syncing)
                    return;

                EditCursor("sel:cursorfillopacity", c => c.FillOpacity = value);
            }
        }

        /// <summary>Size multiplier on the dot held under a pressed button.</summary>
        public double CursorHoldSize
        {
            get => _cursorHoldSize;
            set
            {
                value = Clamp(value, MinHighlightFactor, MaxHighlightFactor);
                if (!Set(ref _cursorHoldSize, value) || _syncing)
                    return;

                EditCursor("sel:cursorholdsize", c => c.HoldSize = value);
            }
        }

        /// <summary>Size multiplier on the animation the release fires.</summary>
        public double CursorClickSize
        {
            get => _cursorClickSize;
            set
            {
                value = Clamp(value, MinHighlightFactor, MaxHighlightFactor);
                if (!Set(ref _cursorClickSize, value) || _syncing)
                    return;

                EditCursor("sel:cursorclicksize", c => c.ClickSize = value);
            }
        }

        /// <summary>Playback-rate multiplier on that animation — 2 runs it in half the time.</summary>
        public double CursorAnimationSpeed
        {
            get => _cursorAnimationSpeed;
            set
            {
                value = Clamp(value, MinHighlightFactor, MaxHighlightFactor);
                if (!Set(ref _cursorAnimationSpeed, value) || _syncing)
                    return;

                EditCursor("sel:cursoranimspeed", c => c.AnimationSpeed = value);
            }
        }

        /// <summary>Keystroke text size, in output-canvas pixels (the preview scales it like a
        /// text card's).</summary>
        public double KeyboardFontSize
        {
            get => _keyboardFontSize;
            set
            {
                value = Clamp(value, MinKeyboardFontSize, MaxKeyboardFontSize);
                if (!Set(ref _keyboardFontSize, value) || _syncing)
                    return;

                EditKeyboard("sel:keyfont", k => k.FontSize = value);
            }
        }

        /// <summary>How long a finished run stays fully visible before it plays its exit
        /// animation (the item's <see cref="Item.Exit"/>, applied per row).</summary>
        public double KeyboardLingerMs
        {
            get => _keyboardLingerMs;
            set
            {
                value = Clamp(value, 0, MaxKeyboardMs);
                if (!Set(ref _keyboardLingerMs, value) || _syncing)
                    return;

                EditKeyboard("sel:keylinger", k => k.LingerMs = MsToInt(value));
            }
        }

        /// <summary>Colour of the typed text in the pill, as <c>#RRGGBB</c> or <c>#AARRGGBB</c>.
        /// Half-typed values stay in the box unwritten, exactly as <see cref="TextColorHex"/>
        /// does. The keycaps a special key draws keep their own fixed livery.</summary>
        public string KeyboardTextColorHex
        {
            get => _keyboardTextColorHex;
            set
            {
                if (!Set(ref _keyboardTextColorHex, value) || _syncing)
                    return;

                if (TryParseArgb(value, out var argb))
                    EditKeyboard("sel:keytextcolor", k => k.TextColor = argb);
            }
        }

        /// <summary>Fill of the typing pill, alpha included — how translucent the block reads is
        /// this colour's own alpha.</summary>
        public string KeyboardBackColorHex
        {
            get => _keyboardBackColorHex;
            set
            {
                if (!Set(ref _keyboardBackColorHex, value) || _syncing)
                    return;

                if (TryParseArgb(value, out var argb))
                    EditKeyboard("sel:keybackcolor", k => k.BackgroundColor = argb);
            }
        }

        /// <summary>The typing gap that ends a run and starts the next row.</summary>
        public double KeyboardPauseBreakMs
        {
            get => _keyboardPauseBreakMs;
            set
            {
                value = Clamp(value, 0, MaxKeyboardMs);
                if (!Set(ref _keyboardPauseBreakMs, value) || _syncing)
                    return;

                EditKeyboard("sel:keypause", k => k.PauseBreakMs = MsToInt(value));
            }
        }

        /// <summary>Which keystrokes the overlay shows, as one of
        /// <see cref="KeystrokeFilterOptions"/>.</summary>
        public KeystrokeFilterOption KeyboardFilter
        {
            get => KeystrokeFilterOptions.First(o => o.Value == _keyboardFilter);
            set
            {
                if (value == null || _syncing || value.Value == _keyboardFilter)
                    return;

                _keyboardFilter = value.Value;
                OnPropertyChanged(nameof(KeyboardFilter));
                EditKeyboard("sel:keyfilter", k => k.Filter = value.Value);
            }
        }

        // ---------------------------------------------------------------------------- surround

        /// <summary>
        /// Whether the SURROUND section is on show. Pictures (video and image items) always offer it;
        /// a cursor row offers it only while a themed glyph is drawn — under <c>native</c> the
        /// recorded box already carries the system cursor's own shadow and there is nothing here to
        /// decorate. Everything else (text, colour, the keystroke block, the effect items) draws no
        /// silhouette the compositor would put a surround around, so the section stays away.
        /// </summary>
        public bool ShowSurround => _surroundSubject == SurroundSubject.Picture
            || (_surroundSubject == SurroundSubject.Cursor && CursorGlyphEnabled);

        /// <summary>The surround tiles, with mask-tile semantics: only a true write applies, and the
        /// radio group's deselection of the losers writes nothing. <see cref="SurroundNone"/> stores a
        /// null surround rather than a kind (<see cref="SurroundKind.None"/> is never written).</summary>
        public bool SurroundNone
        {
            get => _surroundKind == SurroundKind.None;
            set => SetSurroundKind(SurroundKind.None, value);
        }

        public bool SurroundShadow
        {
            get => _surroundKind == SurroundKind.Shadow;
            set => SetSurroundKind(SurroundKind.Shadow, value);
        }

        public bool SurroundGlow
        {
            get => _surroundKind == SurroundKind.Glow;
            set => SetSurroundKind(SurroundKind.Glow, value);
        }

        public bool SurroundOutline
        {
            get => _surroundKind == SurroundKind.Outline;
            set => SetSurroundKind(SurroundKind.Outline, value);
        }

        /// <summary>The colour and size rows: every surround has both, and None has neither.</summary>
        public bool ShowSurroundColor => _surroundKind != SurroundKind.None;

        public bool ShowSurroundSize => _surroundKind != SurroundKind.None;

        /// <summary>The distance row — a shadow's alone. A glow and an outline sit on the item, so
        /// there is nothing to move them by.</summary>
        public bool ShowSurroundDistance => _surroundKind == SurroundKind.Shadow;

        /// <summary>What the size row is called, because the one number means a different thing per
        /// style: how far a shadow or glow bleeds out, how thick an outline is drawn.</summary>
        public string SurroundSizeLabel => _surroundKind switch
        {
            SurroundKind.Glow => "Spread",
            SurroundKind.Outline => "Thickness",
            _ => "Softness",
        };

        /// <summary>The surround colour as <c>#RRGGBB</c> or <c>#AARRGGBB</c> — alpha included, which
        /// is how strongly it reads. A half-typed value stays in the well unwritten, exactly
        /// as <see cref="TextColorHex"/> does.</summary>
        public string SurroundColorHex
        {
            get => _surroundColorHex;
            set
            {
                if (!Set(ref _surroundColorHex, value) || _syncing)
                    return;

                RaiseSurroundPreviews();

                if (TryParseArgb(value, out var argb))
                    EditSurround("sel:surroundcolor", s => s.Color = argb);
            }
        }

        /// <summary>How far the surround spreads, as a fraction of the item's drawn extent (the panel
        /// shows it as a percentage) — see <see cref="SurroundSizeLabel"/> for what it means per
        /// style.</summary>
        public double SurroundSize
        {
            get => _surroundSize;
            set
            {
                value = Clamp(value, 0, MaxSurroundSize);
                if (!Set(ref _surroundSize, value) || _syncing)
                    return;

                RaiseSurroundPreviews();
                EditSurround("sel:surroundsize", s => s.Size = value);
            }
        }

        /// <summary>How far the shadow falls, as the same kind of fraction. The light direction is
        /// the compositor's and fixed (down-right), so distance is the only dial.</summary>
        public double SurroundDistance
        {
            get => _surroundDistance;
            set
            {
                value = Clamp(value, 0, MaxSurroundDistance);
                if (!Set(ref _surroundDistance, value) || _syncing)
                    return;

                RaiseSurroundPreviews();
                EditSurround("sel:surrounddistance", s => s.Distance = value);
            }
        }

        /// <summary>What the three surround rows' reset dots offer: the current style's own starting
        /// numbers for the current kind of item, which is why the dots bind these rather than
        /// carrying a fixed attribute.</summary>
        public string DefaultSurroundColorHex => HexOfArgb(SurroundDefaults().Color);

        public double DefaultSurroundSize => SurroundDefaults().Size;

        public double DefaultSurroundDistance => SurroundDefaults().Distance;

        private (uint Color, double Size, double Distance) SurroundDefaults() =>
            Surround.DefaultsFor(_surroundKind, _surroundSubject == SurroundSubject.Cursor);

        /// <summary>
        /// What each tile draws: the kind that tile stands for, at the numbers it would be drawn
        /// with — the <b>live</b> ones for the style currently picked, so the picked tile follows the
        /// rows below it as they are dialled, and the style's own defaults for the other two (they
        /// have no configuration of their own until they are chosen). The None tile needs none of
        /// this: it draws the bare item.
        /// </summary>
        /// <remarks>A fresh object on every read, which is what makes the tile's binding repaint —
        /// and why these are raised by hand wherever a dial moves.</remarks>
        public Surround SurroundPreviewShadow => SurroundPreviewOf(SurroundKind.Shadow);

        public Surround SurroundPreviewGlow => SurroundPreviewOf(SurroundKind.Glow);

        public Surround SurroundPreviewOutline => SurroundPreviewOf(SurroundKind.Outline);

        private Surround SurroundPreviewOf(SurroundKind kind)
        {
            var cursor = _surroundSubject == SurroundSubject.Cursor;
            if (kind != _surroundKind)
                return Surround.Create(kind, cursor);

            return new Surround
            {
                Kind = kind,
                Color = TryParseArgb(_surroundColorHex, out var argb)
                    ? argb
                    : Surround.DefaultsFor(kind, cursor).Color,
                Size = _surroundSize,
                Distance = _surroundDistance,
            };
        }

        private void RaiseSurroundPreviews()
        {
            OnPropertyChanged(nameof(SurroundPreviewShadow));
            OnPropertyChanged(nameof(SurroundPreviewGlow));
            OnPropertyChanged(nameof(SurroundPreviewOutline));
        }

        // ------------------------------------------------------------------------------- ramps

        /// <summary>Whether the item ramps in at all — the section's switch, and the only thing
        /// that decides whether <see cref="Item.Entry"/> exists. Checking writes the ramp the two
        /// rows below describe, which are sticky, so it is the one the user last had (or the
        /// editor's default the first time); unchecking removes it and leaves them showing it.
        /// The length and easing rows are only shown while this is on.</summary>
        public bool RampEntryEnabled
        {
            get => _rampEntryEnabled;
            set
            {
                if (!Set(ref _rampEntryEnabled, value) || _syncing)
                    return;

                if (value)
                    SeedRampLength(entry: true);

                ApplyRamp(entry: true);
            }
        }

        /// <summary>Length of the entry ramp in milliseconds. Inert while the ramp is off: the
        /// number is remembered, not written, until the checkbox puts it back on the item.</summary>
        public double RampEntryMs
        {
            get => _rampEntryMs;
            set
            {
                value = Clamp(value, MinRampMs, MaxTransitionMs);
                if (!Set(ref _rampEntryMs, value) || _syncing || !_rampEntryEnabled)
                    return;

                ApplyRamp(entry: true);
            }
        }

        public TransitionEasing RampEntryEasing
        {
            get => _rampEntryEasing;
            set
            {
                if (!Set(ref _rampEntryEasing, value) || _syncing || !_rampEntryEnabled)
                    return;

                ApplyRamp(entry: true);
            }
        }

        public bool RampExitEnabled
        {
            get => _rampExitEnabled;
            set
            {
                if (!Set(ref _rampExitEnabled, value) || _syncing)
                    return;

                if (value)
                    SeedRampLength(entry: false);

                ApplyRamp(entry: false);
            }
        }

        public double RampExitMs
        {
            get => _rampExitMs;
            set
            {
                value = Clamp(value, MinRampMs, MaxTransitionMs);
                if (!Set(ref _rampExitMs, value) || _syncing || !_rampExitEnabled)
                    return;

                ApplyRamp(entry: false);
            }
        }

        public TransitionEasing RampExitEasing
        {
            get => _rampExitEasing;
            set
            {
                if (!Set(ref _rampExitEasing, value) || _syncing || !_rampExitEnabled)
                    return;

                ApplyRamp(entry: false);
            }
        }

        // ---------------------------------------------------------------------- session events

        private void Session_SelectionChanged(object sender, EventArgs e)
        {
            // crop mode is a conversation about one item; a new selection ends it.
            CropModeActive = false;
            Sync();
        }

        private void Session_ProjectChanged(object sender, ProjectChangedEventArgs e)
        {
            // our own write: the fields already hold what we sent, and re-reading mid-edit would
            // fight the control the user is holding down.
            if (ReferenceEquals(e.Origin, this))
                return;

            Sync();
        }

        /// <summary>A rolled-back mutation leaves the model as it was — including one of ours, whose
        /// echo the origin check above skips — so re-read unconditionally.</summary>
        private void Session_ValidationFailed(object sender, ValidationFailureEventArgs e) => Sync();

        /// <summary>Re-reads everything from the live model. The only place the backing fields are
        /// written without going to the session.</summary>
        private void Sync()
        {
            _syncing = true;
            try
            {
                var item = SelectedItem;
                var track = SelectedTrack;
                var isAudio = track is { Kind: TrackKind.Audio };
                // an effect item paints nothing and carries no sound: none of the placement, mask,
                // crop, per-clip speed or volume rows mean anything on one, so the whole visual
                // half of the panel is off and only the effect's own section is left.
                var isEffect = item?.Content is SpeedContent or ZoomContent;
                var isText = item?.Content is TextContent;
                // the cursor overlay is placed by the capture data, not by the item: its position,
                // size, aspect, crop and mask all come from the screen row it is synced to, so its
                // visual half is off for the same reason an effect's is. The keystroke overlay is
                // the other way round — Transform IS where the block sits and how wide it wraps —
                // so it keeps placement, and only the picture-shaped rows (aspect/crop/shape,
                // which key off isPicture below) stay away from it.
                var isCursor = item?.Content is CursorContent;
                var visual = item != null && !isAudio && !isEffect && !isCursor;
                var isPicture = visual && item.Content is MediaContent or ImageContent;

                Set(ref _hasSelection, item != null, nameof(HasSelection));
                Set(ref _showTransform, visual, nameof(ShowTransform));
                Set(ref _showScale, visual && !isText, nameof(ShowScale));
                // the keystroke overlay keeps placement but not rotation: the composer draws the
                // block upright whatever Transform.Rotation says (see DrawKeyboard).
                Set(ref _showRotation, visual && item.Content is not KeyboardContent,
                    nameof(ShowRotation));
                Set(ref _showMask, isPicture, nameof(ShowMask));
                Set(ref _showCrop, isPicture, nameof(ShowCrop));
                Set(ref _showText, isText, nameof(ShowText));
                Set(ref _showAudio, isAudio, nameof(ShowAudio));
                Set(ref _showTransitions, visual, nameof(ShowTransitions));
                Set(ref _showRamp, item != null && (isAudio || isEffect), nameof(ShowRamp));
                Set(ref _showSpeedEffect, item?.Content is SpeedContent, nameof(ShowSpeedEffect));
                Set(ref _showZoomEffect, item?.Content is ZoomContent, nameof(ShowZoomEffect));
                Set(ref _showCursorTrack, isCursor, nameof(ShowCursorTrack));
                Set(ref _showKeyboardTrack, item?.Content is KeyboardContent, nameof(ShowKeyboardTrack));
                // the eye stays: hiding an effect row is how the effect is turned off
                Set(ref _showTrackHidden, item != null && !isAudio, nameof(ShowTrackHidden));
                Set(ref _showTrackMuted, isAudio, nameof(ShowTrackMuted));

                Set(ref _subjectName, track?.Name ?? "", nameof(SubjectName));
                Set(ref _subjectKind, DescribeContent(item, isAudio), nameof(SubjectKind));

                Set(ref _trackHidden, track?.Hidden ?? false, nameof(TrackHidden));
                Set(ref _trackMuted, track?.Muted ?? false, nameof(TrackMuted));
                Set(ref _isLinked, item?.LinkGroupId != null, nameof(IsLinked));
                Set(ref _canDesync,
                    _isLinked && item.Content is not CursorContent and not KeyboardContent,
                    nameof(CanDesync));
                CommandUnlink.RaiseCanExecuteChanged();

                var media = item?.Content as MediaContent;
                Set(ref _showSpeed, media != null && item.LinkGroupId == null, nameof(ShowSpeed));
                Set(ref _speed, TimelineOps.SpeedOf(media), nameof(SpeedChoice));

                if (item?.Content is SpeedContent speedEffect)
                    Set(ref _speedFactor, speedEffect.Factor, nameof(SpeedTarget));

                if (item?.Content is ZoomContent zoom)
                {
                    Set(ref _zoomFactor, zoom.Zoom, nameof(ZoomFactor));
                    Set(ref _zoomFocusX, zoom.FocusX, nameof(ZoomFocusX));
                    Set(ref _zoomFocusY, zoom.FocusY, nameof(ZoomFocusY));
                }

                if (item?.Content is CursorContent cursor)
                {
                    Set(ref _cursorStyle, cursor.Style ?? DefaultCursorStyleOption.Value, nameof(CursorStyle));
                    Set(ref _cursorVariant, cursor.Variant, nameof(CursorVariant));
                    Set(ref _cursorSize, cursor.Size, nameof(CursorSize));
                    Set(ref _cursorDebounce, cursor.Debounce, nameof(CursorDebounce));
                    Set(ref _cursorClickAnimation, cursor.ClickAnimation ?? DefaultClickAnimationOption.Value,
                        nameof(CursorClickAnimation));
                    Set(ref _cursorClickColor, cursor.ClickColor, nameof(CursorClickColor));
                    Set(ref _cursorClickColorHex, HexOfArgb(cursor.ClickColor), nameof(CursorClickColorHex));
                    Set(ref _cursorFillOpacity, cursor.FillOpacity, nameof(CursorFillOpacity));
                    Set(ref _cursorHoldSize, cursor.HoldSize, nameof(CursorHoldSize));
                    Set(ref _cursorClickSize, cursor.ClickSize, nameof(CursorClickSize));
                    Set(ref _cursorAnimationSpeed, cursor.AnimationSpeed, nameof(CursorAnimationSpeed));
                    Set(ref _cursorCapturePath,
                        _session?.Project?.Sources?.FirstOrDefault(s => s.Id == cursor.SourceId)?.InputCapturePath,
                        nameof(CursorCapturePath));
                    OnPropertyChanged(nameof(CursorGlyphEnabled));
                    RaiseHighlightRowFlags();
                    OnPropertyChanged(nameof(CursorVariantOptions));
                    OnPropertyChanged(nameof(CursorVariantsVisible));
                }

                if (item?.Content is KeyboardContent keyboard)
                {
                    Set(ref _keyboardFontSize, keyboard.FontSize, nameof(KeyboardFontSize));
                    Set(ref _keyboardLingerMs, keyboard.LingerMs, nameof(KeyboardLingerMs));
                    Set(ref _keyboardPauseBreakMs, keyboard.PauseBreakMs, nameof(KeyboardPauseBreakMs));
                    Set(ref _keyboardFilter, keyboard.Filter, nameof(KeyboardFilter));
                    Set(ref _keyboardTextColorHex, HexOfArgb(keyboard.TextColor), nameof(KeyboardTextColorHex));
                    Set(ref _keyboardBackColorHex, HexOfArgb(keyboard.BackgroundColor), nameof(KeyboardBackColorHex));
                }

                // the surround section is shared by the pictures and the cursor glyph; which of the
                // two it is decides what its dials default to, so the subject is read before them.
                _surroundSubject = isPicture ? SurroundSubject.Picture
                    : isCursor ? SurroundSubject.Cursor
                    : SurroundSubject.None;
                OnPropertyChanged(nameof(ShowSurround));

                var surround = item?.Surround;
                SetSurroundFlags(surround?.Kind ?? SurroundKind.None);
                // no surround = keep whatever the rows last showed (they are hidden anyway), so the
                // numbers do not flicker as the selection moves between bare items
                if (surround != null)
                {
                    Set(ref _surroundColorHex, HexOfArgb(surround.Color), nameof(SurroundColorHex));
                    Set(ref _surroundSize, surround.Size, nameof(SurroundSize));
                    Set(ref _surroundDistance, surround.Distance, nameof(SurroundDistance));
                }

                // the tiles preview the live numbers, and the subject decides what the unpicked
                // styles would start at — both have just moved
                RaiseSurroundPreviews();

                var transform = item?.Transform ?? new Transform();
                Set(ref _positionX, transform.X, nameof(PositionX));
                Set(ref _positionY, transform.Y, nameof(PositionY));
                Set(ref _scale, transform.Scale, nameof(Scale));
                Set(ref _hasScaleY, transform.ScaleY != null, nameof(ShowScaleHeight));
                Set(ref _scaleHeight, transform.ScaleY ?? _scaleHeight, nameof(ScaleHeight));
                OnPropertyChanged(nameof(ShowScaleHeight));
                OnPropertyChanged(nameof(ScaleLabel));
                Set(ref _rotation, transform.Rotation, nameof(Rotation));
                Set(ref _opacity, transform.Opacity, nameof(Opacity));

                var mask = transform.Mask;
                if (mask != null)
                    _rememberedCornerRadius = mask.CornerRadius;
                Set(ref _maskSquare, mask == null, nameof(MaskSquare));
                Set(ref _maskCircle, mask is { Shape: MaskShape.Circle }, nameof(MaskCircle));
                Set(ref _maskRounded, mask is { Shape: MaskShape.RoundedRect }, nameof(MaskRounded));
                Set(ref _maskSquircle, mask is { Shape: MaskShape.Squircle }, nameof(MaskSquircle));
                Set(ref _cornerRadius, mask?.CornerRadius ?? _rememberedCornerRadius, nameof(CornerRadius));
                OnPropertyChanged(nameof(ShowCornerRadius));

                var crop = transform.Crop ?? new CropRect();
                Set(ref _cropLeft, crop.Left, nameof(CropLeft));
                Set(ref _cropTop, crop.Top, nameof(CropTop));
                Set(ref _cropRight, crop.Right, nameof(CropRight));
                Set(ref _cropBottom, crop.Bottom, nameof(CropBottom));
                OnPropertyChanged(nameof(CropTotal));

                SyncAspect(item);

                // crop mode only means anything while a croppable picture is selected
                if (!isPicture)
                    CropModeActive = false;

                if (item?.Content is TextContent text)
                {
                    Set(ref _text, text.Text, nameof(Text));
                    Set(ref _fontFamily, text.Font ?? "", nameof(FontFamily));
                    Set(ref _fontSize, text.Size > 0 ? text.Size : 32, nameof(FontSize));
                    Set(ref _textColorHex, String.IsNullOrEmpty(text.Color) ? "#FFFFFFFF" : text.Color, nameof(TextColorHex));
                    Set(ref _textAlign, text.Align, nameof(TextAlign));
                }

                Set(ref _volume, item?.Volume ?? 1.0, nameof(Volume));

                SyncTransition(item?.Entry, ref _entryKind, ref _entryDurationMs, ref _entryEasing,
                    nameof(EntryKind), nameof(EntryDurationMs), nameof(EntryEasing), nameof(ShowEntryOptions));
                SyncTransition(item?.Exit, ref _exitKind, ref _exitDurationMs, ref _exitEasing,
                    nameof(ExitKind), nameof(ExitDurationMs), nameof(ExitEasing), nameof(ShowExitOptions));

                SyncRamp(item?.Entry, ref _rampEntryEnabled, ref _rampEntryMs, ref _rampEntryEasing,
                    nameof(RampEntryEnabled), nameof(RampEntryMs), nameof(RampEntryEasing));
                SyncRamp(item?.Exit, ref _rampExitEnabled, ref _rampExitMs, ref _rampExitEasing,
                    nameof(RampExitEnabled), nameof(RampExitMs), nameof(RampExitEasing));
            }
            finally
            {
                _syncing = false;
            }
        }

        private void SyncTransition(Transition transition, ref TransitionKind kind, ref double durationMs,
            ref TransitionEasing easing, string kindName, string durationName, string easingName, string showName)
        {
            Set(ref kind, transition?.Kind ?? TransitionKind.None, kindName);
            // no transition = keep whatever the spinners last showed, so turning one back on offers
            // the numbers the user was working with rather than snapping to the defaults.
            if (transition != null)
            {
                Set(ref durationMs, transition.DurationTicks / (double)TimeSpan.TicksPerMillisecond, durationName);
                Set(ref easing, transition.Easing, easingName);
            }

            OnPropertyChanged(showName);
        }

        /// <summary>The ramp's half of the same read: the checkbox says whether the item has one,
        /// and both values are sticky — an item without a ramp leaves the length and easing rows
        /// showing whatever they last did (they are hidden anyway), so ticking the box back on
        /// offers the ramp the user was working with rather than the defaults.</summary>
        private void SyncRamp(Transition transition, ref bool enabled, ref double durationMs,
            ref TransitionEasing easing, string enabledName, string durationName, string easingName)
        {
            Set(ref enabled, transition != null, enabledName);

            if (transition != null)
            {
                Set(ref durationMs, transition.DurationTicks / (double)TimeSpan.TicksPerMillisecond, durationName);
                Set(ref easing, transition.Easing, easingName);
            }
        }

        // ------------------------------------------------------------------------- write paths

        /// <summary>The row-wide write (see the class remarks): every linked segment of the
        /// selected item's row in one mutation, or just the item when it is unlinked.</summary>
        private void EditRow(string coalesceKey, Action<Item> edit) => EditRow(coalesceKey, edit, this);

        /// <summary>
        /// Same, with an explicit origin. Spinner-backed setters pass <c>this</c> so the echo does
        /// not fight the control being held; the aspect/crop writes pass null instead — they change
        /// state whose mirrors (tile selection, crop total, the height row) the setters do not
        /// maintain by hand, so the inspector wants its own <see cref="Session_ProjectChanged"/>
        /// re-read, exactly like <see cref="Unlink"/>.
        /// </summary>
        private void EditRow(string coalesceKey, Action<Item> edit, object origin)
        {
            var item = SelectedItem;
            if (item == null)
                return;

            // scoped to what the write reaches (the row for linked segments, the item alone
            // otherwise): a bare "sel:x" would let a selection change inside the coalesce window
            // merge two different items' edits into one undo entry.
            var scope = item.LinkGroupId != null ? item.TrackId : item.Id;
            _session.EditItems(ItemRowScope.RowItemIds(_session, item), edit, $"{coalesceKey}:{scope}",
                structural: false, origin: origin);
        }

        /// <summary>The single-item write: properties of this segment alone. The coalesce key is
        /// item-scoped for the same reason <see cref="EditRow"/>'s is row-scoped.</summary>
        private void EditSelected(string coalesceKey, Action<Item> edit)
        {
            var item = SelectedItem;
            if (item == null)
                return;

            _session.EditItem(item.Id, edit, $"{coalesceKey}:{item.Id}", structural: false, origin: this);
        }

        /// <summary>The text-card write. Content-guarded on both sides: the text section's bindings
        /// stay live while it is hidden, so a stale write must find nothing to do rather than
        /// throw at a pointer handler.</summary>
        private void EditText(string coalesceKey, Action<TextContent> edit)
        {
            var item = SelectedItem;
            if (item?.Content is not TextContent)
                return;

            _session.EditItem(item.Id, i =>
            {
                if (i.Content is TextContent text)
                    edit(text);
            }, $"{coalesceKey}:{item.Id}", structural: false, origin: this);
        }

        /// <summary>The zoom write, content-guarded on both sides like <see cref="EditText"/>: the
        /// zoom section's bindings stay live while it is hidden, so a stale write must find nothing
        /// to do.</summary>
        private void EditZoom(string coalesceKey, Action<ZoomContent> edit)
        {
            var item = SelectedItem;
            if (item?.Content is not ZoomContent)
                return;

            _session.EditItem(item.Id, i =>
            {
                if (i.Content is ZoomContent zoom)
                    edit(zoom);
            }, $"{coalesceKey}:{item.Id}", structural: false, origin: this);
        }

        /// <summary>The cursor-overlay write: content-guarded on both sides like
        /// <see cref="EditZoom"/> (the section's bindings stay live while it is hidden), and
        /// row-wide like <see cref="EditRow"/> — the split segments of an overlay row are one
        /// overlay, so they must not end up with different styles.</summary>
        private void EditCursor(string coalesceKey, Action<CursorContent> edit)
        {
            EditOverlayRow<CursorContent>(coalesceKey, edit);
        }

        /// <summary>The keystroke-overlay write; see <see cref="EditCursor"/>.</summary>
        private void EditKeyboard(string coalesceKey, Action<KeyboardContent> edit)
        {
            EditOverlayRow<KeyboardContent>(coalesceKey, edit);
        }

        private void EditOverlayRow<TContent>(string coalesceKey, Action<TContent> edit)
            where TContent : ItemContent
        {
            var item = SelectedItem;
            if (item?.Content is not TContent)
                return;

            var scope = item.LinkGroupId != null ? item.TrackId : item.Id;
            _session.EditItems(ItemRowScope.RowItemIds(_session, item), i =>
            {
                if (i.Content is TContent content)
                    edit(content);
            }, $"{coalesceKey}:{scope}", structural: false, origin: this);
        }

        /// <summary>Tile setter body: mask-tile semantics (only a true write applies), then the new
        /// style's own starting numbers. Nothing carries over from the style being left behind — the
        /// two dials mean different things per style (see <see cref="Surround.DefaultsFor"/>), so
        /// a remembered softness would arrive as a nonsense thickness.</summary>
        private void SetSurroundKind(SurroundKind kind, bool selected)
        {
            if (!selected)
            {
                // a radio group deselecting the loser; the winner's own set does the work
                if (_surroundKind == kind && !_syncing)
                    OnPropertyChanged(SurroundTileProperty(kind));
                return;
            }

            if (_surroundKind == kind)
                return;

            SetSurroundFlags(kind);

            if (_syncing)
                return;

            var (color, size, distance) = SurroundDefaults();
            Set(ref _surroundColorHex, HexOfArgb(color), nameof(SurroundColorHex));
            Set(ref _surroundSize, size, nameof(SurroundSize));
            Set(ref _surroundDistance, distance, nameof(SurroundDistance));
            // AFTER the seeding, not only inside SetSurroundFlags above: the newly picked tile
            // previews the live dials, and until this line they are still the previous style's (a
            // tile picked from None would preview a zero-sized surround, which draws nothing).
            RaiseSurroundPreviews();
            ApplySurround();
        }

        /// <summary>Moves the selection to <paramref name="kind"/>, raising the tiles that flipped
        /// plus everything the kind decides (which rows show, what the size row is called, what the
        /// dots reset to).</summary>
        private void SetSurroundFlags(SurroundKind kind)
        {
            var previous = _surroundKind;
            _surroundKind = kind;

            foreach (var name in new[] { previous, kind }.Distinct().Select(SurroundTileProperty))
                OnPropertyChanged(name);

            OnPropertyChanged(nameof(ShowSurroundColor));
            OnPropertyChanged(nameof(ShowSurroundSize));
            OnPropertyChanged(nameof(ShowSurroundDistance));
            OnPropertyChanged(nameof(SurroundSizeLabel));
            OnPropertyChanged(nameof(DefaultSurroundColorHex));
            OnPropertyChanged(nameof(DefaultSurroundSize));
            OnPropertyChanged(nameof(DefaultSurroundDistance));
            // the picked tile previews the live numbers and the others their own defaults, so which
            // tile is picked changes what all three of them draw
            RaiseSurroundPreviews();
        }

        private static string SurroundTileProperty(SurroundKind kind) => kind switch
        {
            SurroundKind.Shadow => nameof(SurroundShadow),
            SurroundKind.Glow => nameof(SurroundGlow),
            SurroundKind.Outline => nameof(SurroundOutline),
            _ => nameof(SurroundNone),
        };

        /// <summary>Writes the surround the section currently describes, or removes it for None
        /// (null, not a stored kind of None — "nothing around it" has exactly one representation on
        /// disk, the trade the crop and the ramps make). Built inside the mutation so a replayed edit
        /// can never hand two projects the same object.</summary>
        private void ApplySurround()
        {
            if (_surroundKind == SurroundKind.None)
            {
                EditRow("sel:surround", i => i.Surround = null);
                return;
            }

            var kind = _surroundKind;
            var color = TryParseArgb(_surroundColorHex, out var argb) ? argb : SurroundDefaults().Color;
            var size = _surroundSize;
            var distance = _surroundDistance;

            EditRow("sel:surround", i => i.Surround = new Surround
            {
                Kind = kind,
                Color = color,
                Size = size,
                Distance = distance,
            });
        }

        /// <summary>A surround dial's write: row-wide like every other property of how the row's
        /// picture is drawn, and a no-op on an item with no surround — the section's bindings stay
        /// live while it is hidden, so a stale write must find nothing to do
        /// (<see cref="EditText"/>'s rule).</summary>
        private void EditSurround(string coalesceKey, Action<Surround> edit)
        {
            EditRow(coalesceKey, i =>
            {
                if (i.Surround != null)
                    edit(i.Surround);
            });
        }

        private void SetMaskFlags(bool square, bool circle, bool rounded, bool squircle)
        {
            Set(ref _maskSquare, square, nameof(MaskSquare));
            Set(ref _maskCircle, circle, nameof(MaskCircle));
            Set(ref _maskRounded, rounded, nameof(MaskRounded));
            Set(ref _maskSquircle, squircle, nameof(MaskSquircle));
            OnPropertyChanged(nameof(ShowCornerRadius));
        }

        /// <summary>Writes a mask of the given shape, carrying the radius across the flip — it is
        /// meaningless for a circle, but losing the user's number to a round trip through Circle
        /// is worse than storing an unused one (the v1 webcam pane made the same trade).</summary>
        private void ApplyMaskShape(MaskShape shape)
        {
            var radius = Clamp(_cornerRadius, 0, 0.5);
            _rememberedCornerRadius = radius;

            EditRow("sel:mask", i => TransformOf(i).Mask = new Mask { Shape = shape, CornerRadius = radius });
        }

        /// <summary>Kind None removes the transition entirely (null, not a Kind=None object);
        /// turning one on builds it from the current duration/easing, which
        /// <see cref="SyncTransition"/> left at the editor defaults for an item that never had
        /// one.</summary>
        private void ApplyTransition(bool entry, TransitionKind kind)
        {
            if (kind == TransitionKind.None)
            {
                if (entry)
                    EditSelected("sel:entry", i => i.Entry = null);
                else
                    EditSelected("sel:exit", i => i.Exit = null);
                return;
            }

            var durationMs = entry ? _entryDurationMs : _exitDurationMs;
            var easing = entry ? _entryEasing : _exitEasing;
            if (durationMs <= 0)
            {
                durationMs = DefaultTransitionMs;
                if (entry)
                    Set(ref _entryDurationMs, durationMs, nameof(EntryDurationMs));
                else
                    Set(ref _exitDurationMs, durationMs, nameof(ExitDurationMs));
            }

            var ticks = MsToTicks(durationMs);
            if (entry)
                EditSelected("sel:entry", i => i.Entry = new Transition { Kind = kind, DurationTicks = ticks, Easing = easing });
            else
                EditSelected("sel:exit", i => i.Exit = new Transition { Kind = kind, DurationTicks = ticks, Easing = easing });
        }

        /// <summary>Writes the ramp the section currently describes: switched off it is removed
        /// (null, not a zero-length transition — "no ramp" has one representation on disk, the same
        /// trade the crop makes), switched on it stores a fresh <see cref="TransitionKind.Ramp"/>
        /// of the length and easing the rows show. The transition is built inside the mutation so a
        /// replayed edit can never hand two projects the same object.</summary>
        /// <summary>A ramp being switched on must have a length to be: the sticky one if there is
        /// one, the editor's default otherwise — which is where a never-touched section, and an
        /// item read with a zero-length ramp, both start.</summary>
        private void SeedRampLength(bool entry)
        {
            if (entry)
            {
                if (_rampEntryMs < MinRampMs)
                    Set(ref _rampEntryMs, DefaultTransitionMs, nameof(RampEntryMs));
            }
            else if (_rampExitMs < MinRampMs)
            {
                Set(ref _rampExitMs, DefaultTransitionMs, nameof(RampExitMs));
            }
        }

        private void ApplyRamp(bool entry)
        {
            var enabled = entry ? _rampEntryEnabled : _rampExitEnabled;
            var ticks = MsToTicks(entry ? _rampEntryMs : _rampExitMs);
            var easing = entry ? _rampEntryEasing : _rampExitEasing;

            Transition Build() => !enabled
                ? null
                : new Transition { Kind = TransitionKind.Ramp, DurationTicks = ticks, Easing = easing };

            if (entry)
                EditSelected("sel:rampentry", i => i.Entry = Build());
            else
                EditSelected("sel:rampexit", i => i.Exit = Build());
        }

        private void Unlink()
        {
            var item = SelectedItem;
            var track = SelectedTrack;
            if (item == null || track == null || item.LinkGroupId == null)
                return;

            // origin null (not this): unlinking changes state the setters do not mirror, so the
            // inspector wants its own ProjectChanged re-read.
            _session.UnlinkTrack(track.Id, null);
        }

        // ----------------------------------------------------------------------- model helpers

        /// <summary>Always re-resolved: undo/redo replace both the project and its items.</summary>
        private Item SelectedItem => _session?.PrimarySelectedItem;

        private Track SelectedTrack
        {
            get
            {
                var item = SelectedItem;
                return item == null ? null : _session.Project.Tracks.FirstOrDefault(t => t.Id == item.TrackId);
            }
        }

        private static Transform TransformOf(Item item) => item.Transform ??= new Transform();

        /// <summary>Writes one crop inset, then drops the whole <see cref="CropRect"/> again when
        /// every inset is back to zero. An all-zero crop composes identically to no crop, but
        /// leaving the object behind would put an inert block in every saved project the moment a
        /// spinner was touched — "no crop" has exactly one representation on disk.</summary>
        private static void SetCrop(Item item, Action<CropRect> apply)
        {
            var transform = TransformOf(item);
            var crop = transform.Crop ??= new CropRect();
            apply(crop);

            if (crop.Left == 0 && crop.Top == 0 && crop.Right == 0 && crop.Bottom == 0)
                transform.Crop = null;
        }

        private static long MsToTicks(double ms) =>
            (long)Math.Round(Math.Max(0, ms) * TimeSpan.TicksPerMillisecond);

        /// <summary>The overlay timings are whole milliseconds in the model; a spinner's text box
        /// hands over whatever parsed.</summary>
        private static int MsToInt(double ms) => (int)Math.Round(Math.Max(0, ms));

        /// <summary>Wraps the model's own value list in labelled singletons, in the model's order —
        /// see <see cref="NamedOption"/>.</summary>
        private static IReadOnlyList<NamedOption> BuildOptions(
            IReadOnlyList<string> values, IReadOnlyDictionary<string, string> labels)
        {
            var options = new List<NamedOption>(values.Count);
            foreach (var value in values)
                options.Add(new NamedOption(value, labels.TryGetValue(value, out var label) ? label : value));
            return options;
        }

        /// <summary>The singleton for a wire value — the first entry when the model holds something
        /// the menu does not offer, which is how the composer treats it too (an unknown style falls
        /// back to the theme's arrow rather than drawing nothing).</summary>
        private static NamedOption FindOption(IReadOnlyList<NamedOption> options, string value)
        {
            foreach (var option in options)
            {
                if (option.Value == value)
                    return option;
            }
            return options.Count > 0 ? options[0] : null;
        }

        private static string DescribeContent(Item item, bool onAudioTrack) => item?.Content switch
        {
            null => "",
            MediaContent => onAudioTrack ? "Audio" : "Video",
            TextContent => "Text",
            ImageContent => "Image",
            SolidContent => "Color",
            SpeedContent => "Speed",
            ZoomContent => "Zoom",
            CursorContent => "Cursor",
            KeyboardContent => "Keys",
            _ => "Item",
        };

        /// <summary>#RGB-family colour literal check — the model stores <c>#AARRGGBB</c> strings and
        /// the renderer parses them, so anything else must not reach the model.</summary>
        private static bool IsHexColor(string value)
        {
            if (String.IsNullOrEmpty(value))
                return false;

            var text = value.Trim();
            if (text.Length is not (7 or 9) || text[0] != '#')
                return false;

            for (var i = 1; i < text.Length; i++)
            {
                if (!Uri.IsHexDigit(text[i]))
                    return false;
            }

            return true;
        }

        /// <summary>The same literal, packed for the contents that store a colour as a number
        /// rather than a string (<see cref="KeyboardContent"/>). A <c>#RRGGBB</c> without an alpha
        /// is opaque.</summary>
        private static bool TryParseArgb(string value, out uint argb)
        {
            argb = 0;
            if (!IsHexColor(value))
                return false;

            var digits = value.Trim().Substring(1);
            if (digits.Length == 6)
                digits = "FF" + digits;

            return UInt32.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out argb);
        }

        private static string HexOfArgb(uint argb) => "#" + argb.ToString("X8",
            System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Math.Clamp with NaN collapsing to the lower bound — a spinner's text box hands
        /// over whatever parsed, and a NaN must not be able to poison the project.</summary>
        private static double Clamp(double value, double min, double max) =>
            Double.IsNaN(value) ? min : Math.Clamp(value, min, max);
    }
}
