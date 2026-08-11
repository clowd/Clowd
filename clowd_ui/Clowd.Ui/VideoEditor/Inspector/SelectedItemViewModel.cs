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

        public const double MinScale = 0.01;
        public const double MaxScale = 4.0;
        public const double MaxVolume = 2.0;
        public const double MaxTransitionMs = 10_000;

        /// <summary>Largest inset per side: a crop that reached 1 would leave nothing to draw.</summary>
        public const double MaxCropInset = 0.95;

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
        private double _rotation;
        private double _opacity = 1.0;

        private bool _maskNone = true;
        private bool _maskCircle;
        private bool _maskRounded;
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

        /// <summary>Shown for every visual item, text included: the gizmo's corner drag writes
        /// <c>Transform.Scale</c> for text too, and a value no field shows is a value the user
        /// cannot see or reset. For text it multiplies the natural block size rather than mapping
        /// to a canvas-width fraction (see <c>FrameComposer.DrawText</c>), which is what
        /// <see cref="ScaleLabel"/> distinguishes.</summary>
        public bool ShowScale => _showScale;

        /// <summary>The PLACEMENT scale row's label: "Size" for pictures (canvas-width fraction),
        /// "Text scale" for text (multiplier of the natural block) — so it cannot be confused with
        /// the TEXT section's own font-size "Size".</summary>
        public string ScaleLabel => _showText ? "Text scale" : "Size";

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

        public bool MaskNone
        {
            get => _maskNone;
            set
            {
                // radio group: the two it deselects report false, and only the selected one is an
                // edit — otherwise every flip would write the model twice.
                if (!Set(ref _maskNone, value) || _syncing || !value)
                    return;

                SetMaskFlags(none: true, circle: false, rounded: false);
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

                SetMaskFlags(none: false, circle: true, rounded: false);
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

                SetMaskFlags(none: false, circle: false, rounded: true);
                ApplyMaskShape(MaskShape.RoundedRect);
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

        // -------------------------------------------------------------------------------- crop

        public double CropLeft
        {
            get => _cropLeft;
            set
            {
                value = Clamp(value, 0, MaxCropInset);
                if (!Set(ref _cropLeft, value) || _syncing)
                    return;

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

                EditRow("sel:cropb", i => SetCrop(i, c => c.Bottom = value));
            }
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
                value = Clamp(value, 1, 2000);
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

        // ---------------------------------------------------------------------- session events

        private void Session_SelectionChanged(object sender, EventArgs e) => Sync();

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
                Set(ref _showScale, visual, nameof(ShowScale));
                Set(ref _showMask, isPicture, nameof(ShowMask));
                Set(ref _showCrop, isPicture, nameof(ShowCrop));
                Set(ref _showText, isText, nameof(ShowText));
                OnPropertyChanged(nameof(ScaleLabel));
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

                var transform = item?.Transform ?? new Transform();
                Set(ref _positionX, transform.X, nameof(PositionX));
                Set(ref _positionY, transform.Y, nameof(PositionY));
                Set(ref _scale, transform.Scale, nameof(Scale));
                Set(ref _rotation, transform.Rotation, nameof(Rotation));
                Set(ref _opacity, transform.Opacity, nameof(Opacity));

                var mask = transform.Mask;
                if (mask != null)
                    _rememberedCornerRadius = mask.CornerRadius;
                Set(ref _maskNone, mask == null, nameof(MaskNone));
                Set(ref _maskCircle, mask is { Shape: MaskShape.Circle }, nameof(MaskCircle));
                Set(ref _maskRounded, mask is { Shape: MaskShape.RoundedRect }, nameof(MaskRounded));
                Set(ref _cornerRadius, mask?.CornerRadius ?? _rememberedCornerRadius, nameof(CornerRadius));
                OnPropertyChanged(nameof(ShowCornerRadius));

                var crop = transform.Crop ?? new CropRect();
                Set(ref _cropLeft, crop.Left, nameof(CropLeft));
                Set(ref _cropTop, crop.Top, nameof(CropTop));
                Set(ref _cropRight, crop.Right, nameof(CropRight));
                Set(ref _cropBottom, crop.Bottom, nameof(CropBottom));

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
        private void EditRow(string coalesceKey, Action<Item> edit)
        {
            var item = SelectedItem;
            if (item == null)
                return;

            // scoped to what the write reaches (the row for linked segments, the item alone
            // otherwise): a bare "sel:x" would let a selection change inside the coalesce window
            // merge two different items' edits into one undo entry.
            var scope = item.LinkGroupId != null ? item.TrackId : item.Id;
            _session.EditItems(ItemRowScope.RowItemIds(_session, item), edit, $"{coalesceKey}:{scope}",
                structural: false, origin: this);
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

        private void SetMaskFlags(bool none, bool circle, bool rounded)
        {
            Set(ref _maskNone, none, nameof(MaskNone));
            Set(ref _maskCircle, circle, nameof(MaskCircle));
            Set(ref _maskRounded, rounded, nameof(MaskRounded));
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
