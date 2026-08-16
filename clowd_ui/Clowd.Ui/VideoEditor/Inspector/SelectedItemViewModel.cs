using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.Helpers;
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

        public const double MinScale = 0.01;
        public const double MaxScale = 4.0;
        public const double MaxVolume = 2.0;
        public const double MaxTransitionMs = 10_000;

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

        private bool _showSpeed;
        private double _speed = 1.0;

        public SelectedItemViewModel()
        {
            CommandUnlink = new RelayCommand
            {
                Executed = _ => Unlink(),
                CanExecute = _ => IsLinked,
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

        public bool ShowTransitions => _showTransitions;

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
                var isText = item?.Content is TextContent;
                var visual = item != null && !isAudio;
                var isPicture = visual && item.Content is MediaContent or ImageContent;

                Set(ref _hasSelection, item != null, nameof(HasSelection));
                Set(ref _showTransform, visual, nameof(ShowTransform));
                Set(ref _showScale, visual && !isText, nameof(ShowScale));
                Set(ref _showMask, isPicture, nameof(ShowMask));
                Set(ref _showCrop, isPicture, nameof(ShowCrop));
                Set(ref _showText, isText, nameof(ShowText));
                Set(ref _showAudio, isAudio, nameof(ShowAudio));
                Set(ref _showTransitions, item != null, nameof(ShowTransitions));
                Set(ref _showTrackHidden, visual, nameof(ShowTrackHidden));
                Set(ref _showTrackMuted, isAudio, nameof(ShowTrackMuted));

                Set(ref _subjectName, track?.Name ?? "", nameof(SubjectName));
                Set(ref _subjectKind, DescribeContent(item, isAudio), nameof(SubjectKind));

                Set(ref _trackHidden, track?.Hidden ?? false, nameof(TrackHidden));
                Set(ref _trackMuted, track?.Muted ?? false, nameof(TrackMuted));
                Set(ref _isLinked, item?.LinkGroupId != null, nameof(IsLinked));
                CommandUnlink.RaiseCanExecuteChanged();

                var media = item?.Content as MediaContent;
                Set(ref _showSpeed, media != null && item.LinkGroupId == null, nameof(ShowSpeed));
                Set(ref _speed, TimelineOps.SpeedOf(media), nameof(SpeedChoice));

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

        private static string DescribeContent(Item item, bool onAudioTrack) => item?.Content switch
        {
            null => "",
            MediaContent => onAudioTrack ? "Audio" : "Video",
            TextContent => "Text",
            ImageContent => "Image",
            SolidContent => "Color",
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

        /// <summary>Math.Clamp with NaN collapsing to the lower bound — a spinner's text box hands
        /// over whatever parsed, and a NaN must not be able to poison the project.</summary>
        private static double Clamp(double value, double min, double max) =>
            Double.IsNaN(value) ? min : Math.Clamp(value, min, max);
    }
}
