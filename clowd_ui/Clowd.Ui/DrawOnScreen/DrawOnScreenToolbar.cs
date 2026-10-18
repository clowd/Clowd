using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.Localization;
using Clowd.UI.Controls.Tray;
using Clowd.UI.Helpers;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// The draw-on-screen toolbar: a two-lane strip behind a full-height grip. The primary lane is
    /// the emblem, the two modes (click-through, hide) and the nine tools at full size; the compact
    /// secondary lane is the colours, the sizes, the show-keys toggle and the ways out (undo, clear,
    /// close).
    /// <para>
    /// It owns no state. Everything it shows is read off <see cref="IDrawOnScreenController"/> in
    /// <see cref="Refresh"/>, which runs on every <see cref="IDrawOnScreenController.Changed"/>, and
    /// every click is a controller command — so the session stays the one writer of the mode and the
    /// toolbar can never show a state the canvases are not in. Unlike the other strips a click gives it
    /// the keyboard (<see cref="FloatingTrayOptions.TakesFocus"/>): while the session has the keyboard, or
    /// a tool is in hand, the strip is engaged — lit with the engaged frame, the primary lane's keys
    /// hinted on its tiles — which is when its hotkeys (<see cref="DrawHotkeys"/>) are live (issue #110).
    /// It is the only draw-on-screen window kept
    /// out of screen captures: the ink must stay in the picture, the toolbar must not.
    /// </para>
    /// <para>
    /// Over a frozen screen (<see cref="DrawOnScreenState.Frozen"/>, fixed for the session's life and
    /// known before the strip is built) the click-through, hide and show-keys tiles are left out, and
    /// the divider after the modes with them: none of them applies, and Esc closes instead.
    /// </para>
    /// </summary>
    public sealed class DrawOnScreenToolbar : FloatingTrayWindow
    {
        // the mockup's `.tb.mini svg.glyph`: the stroked close X is drawn a size up from the filled mini
        // icons and with a 2-unit stroke, so it carries the same weight as Undo/Delete beside it.
        // Kept out of TrayGlyphs.All on purpose (TrayGlyphsTests pins that set and its 1.8 stroke).
        private const double MiniStrokeGlyphSize = 16;
        private static readonly TrayGlyph MiniX = new TrayGlyph("x-mini", TrayGlyphs.X.StrokePaths.ToArray(), null, 2.0);

        // the text tool's and the click-through pointer's glyphs, below the other hinted tiles'
        // (TrayTokens.KeyHintGlyphSize, 17)
        private const double TextGlyphSize = 14, PointerGlyphSize = 15.5;

        // the mockup's gap between neighbouring colour and size tiles (its `.seg { gap: 2px }`)
        private const double DotSpacing = 2;

        private readonly IDrawOnScreenController _controller;
        private readonly DrawHotkeys _hotkeys;

        private readonly TrayEmblem _emblem;
        private readonly TrayButton _clickThrough;
        private readonly TrayButton _hide;
        private readonly TrayButton _keys;
        private readonly List<(ToolType Tool, TrayButton Button)> _tools = new();

        // the primary lane's tiles, the ones whose keys are hinted, in strip order
        private readonly List<TrayButton> _hinted = new();
        private readonly List<TraySwatch> _swatches = new();
        private readonly List<TrayDotButton> _sizes = new();
        private readonly TrayButton _undo;
        private readonly TrayButton _clear;
        private readonly TrayButton _close;

        public DrawOnScreenToolbar(IDrawOnScreenController controller)
            : base(new FloatingTrayOptions
            {
                Title = Loc.T("Draw_WindowTitle"),
                HasEmblem = false,
                GripLayout = TrayGripLayout.Spanning,
                Shadow = TrayTokens.ShadowCompact,
                Intro = true,
                EngagedFrame = true,
                TakesFocus = true,
            })
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _hotkeys = new DrawHotkeys(controller);

            // ---- primary lane: emblem · click-through · hide | tools ----
            // the emblem lives in the lane rather than on the chassis (HasEmblem is off) so it opens
            // the first row instead of standing beside both. Its margin is set per axis, see
            // ApplyAxisMargins.
            _emblem = new TrayEmblem();

            // click-through is a mode the strip shows with a ring rather than the accent fill: the
            // fill belongs to the lit tool, and both can be read at once (see Refresh). The pointer
            // has no tail, which would run into the accelerator hint in the tile's corner.
            _clickThrough = new TrayButton
            {
                Icon = (Geometry)Application.Current.FindResource("IconToolPointerTailless"),
            };
            // the mockup's 1.5 px optical shift for the pointer's lopsided silhouette
            _clickThrough.Classes.Add("nudge");
            _clickThrough.Click += (s, e) => _controller.ToggleClickThrough();

            _hide = new TrayButton();
            _hide.Click += (s, e) => _controller.ToggleHide();

            var frozen = _controller.State.Frozen;
            var primary = new DrawLanePanel { Spacing = TrayTokens.Gap };
            primary.Children.Add(_emblem);
            if (!frozen)
            {
                Hint(_clickThrough, DrawHotkeys.ClickThrough);
                // the solid pointer reads heavier than its outlined neighbours at the shared size
                _clickThrough.GlyphSize = PointerGlyphSize;
                Hint(_hide, DrawHotkeys.Hide);
                primary.Children.Add(_clickThrough);
                primary.Children.Add(_hide);

                // it parts the modes from the tools; with no modes the emblem leads straight into them
                primary.Children.Add(new TrayDivider());
            }

            foreach (var tool in DrawPalette.Tools)
            {
                var button = new TrayButton
                {
                    Icon = (Geometry)Application.Current.FindResource(DrawPalette.ToolIconKey(tool)),
                };
                button.Click += (s, e) => _controller.PickTool(tool);
                Hint(button, DrawHotkeys.Tool(tool));

                // the T fills its whole box edge to edge, so at the shared size it looms over its
                // neighbours and over its hint
                if (tool == ToolType.Text)
                    button.GlyphSize = TextGlyphSize;
                _tools.Add((tool, button));
                primary.Children.Add(button);
            }

            // ---- secondary lane: colours | sizes | keys · undo · clear · close ----
            // the mockup's 2 px between neighbouring colours and sizes; the ways out sit 4 px apart,
            // which ApplyAxisMargins tops up
            var secondary = new TrayZonePanel { Spacing = DotSpacing };

            for (var i = 0; i < DrawPalette.Colors.Count; i++)
            {
                var index = i;
                var swatch = new TraySwatch { DotColor = DrawPalette.Colors[i] };
                swatch.Click += (s, e) => _controller.SelectColor(index);
                TrayZonePanel.SetZone(swatch, TrayZone.Start);
                _swatches.Add(swatch);
                secondary.Children.Add(swatch);
            }

            for (var i = 0; i < DrawPalette.Sizes.Count; i++)
            {
                var index = i;
                var size = new TrayDotButton { DotDiameter = DrawPalette.Sizes[i].PreviewDot };
                size.Click += (s, e) => _controller.SelectSize(index);
                TrayZonePanel.SetZone(size, TrayZone.Center);
                _sizes.Add(size);
                secondary.Children.Add(size);
            }

            // shows the keys being pressed over the screen; lit while on, like hide
            _keys = NewMini();
            _keys.Icon = (Geometry)Application.Current.FindResource("IconKeyboard");
            _keys.Click += (s, e) => _controller.ToggleKeys();

            _undo = NewMini();
            _undo.Icon = (Geometry)Application.Current.FindResource("IconUndo");
            _undo.Click += (s, e) => _controller.Undo();

            _clear = NewMini();
            _clear.Icon = (Geometry)Application.Current.FindResource("IconDelete");
            _clear.Click += (s, e) => _controller.ClearAll();

            _close = NewMini();
            _close.Glyph = MiniX;
            _close.GlyphSize = MiniStrokeGlyphSize; // a local value beats the .mini class setter (15)
            _close.Look = TrayButtonLook.Danger;
            _close.Click += (s, e) => _controller.Close();

            foreach (var way in frozen ? new[] { _undo, _clear, _close } : new[] { _keys, _undo, _clear, _close })
            {
                TrayZonePanel.SetZone(way, TrayZone.End);
                secondary.Children.Add(way);
            }

            // both lanes are filled before they join the deck: the deck forwards its axis into its
            // subtree when a lane is added and on a rotation, never when a lane's own children change.
            var deck = new TrayDeck();
            deck.Children.Add(primary);
            deck.Children.Add(secondary);
            Tray.Items.Add(deck);

            // the deck is a direct item, so the tray pushes a rotation into it and the deck carries it
            // on; only the two optical margins are this strip's own business.
            Tray.PropertyChanged += (s, e) =>
            {
                if (e.Property == FloatingTray.OrientationProperty)
                    ApplyAxisMargins();
            };
            ApplyAxisMargins();

            // every key is the strip's own while it is focused: a hotkey, or nothing — a key must never
            // fall through to the button the last click focused, or Space would press Clear all again
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

            _controller.Changed += OnControllerChanged;
            Loc.CultureChanged += OnCultureChanged;
            Refresh();
        }

        /// <summary>
        /// A strip losing the keyboard forgets the keys held on it: their key-ups land elsewhere. Whether
        /// the strip is engaged is the session's to say (<see cref="IDrawOnScreenController.IsFocused"/>),
        /// as the keyboard can be in a canvas window instead. Not the Deactivated event: Avalonia raises
        /// it before it clears <see cref="WindowBase.IsActive"/>.
        /// </summary>
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsActiveProperty && !IsActive)
                _hotkeys?.Reset();
        }

        protected override void OnClosed(EventArgs e)
        {
            _controller.Changed -= OnControllerChanged;
            Loc.CultureChanged -= OnCultureChanged;
            base.OnClosed(e);
        }

        private void OnControllerChanged(object sender, EventArgs e) => Refresh();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            _hotkeys.KeyDown(e.Key, e.KeyModifiers);
            e.Handled = true;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            _hotkeys.KeyUp(e.Key);
            e.Handled = true;
        }

        /// <summary>Engaged while the session has the keyboard or a tool is in hand: the times its hotkeys
        /// are live (<see cref="DrawOnScreenState.AreHotkeysLive"/>), and the primary lane's hints are up.</summary>
        private void UpdateEngaged()
        {
            var engaged = _controller.State.AreHotkeysLive(_controller.IsFocused);
            IsEngaged = engaged;
            foreach (var button in _hinted)
                button.IsAccessKeyShown = engaged;
        }

        private void OnCultureChanged(object sender, EventArgs e)
        {
            Title = Loc.T("Draw_WindowTitle");
            Refresh();
        }

        /// <summary>
        /// Writes the controller's state onto every tile: which tool is lit, the click-through ring,
        /// the hide glyph, the selected colour and size, the size dots' colour, whether undo and clear
        /// have anything to do, and every tooltip and accessible name (they follow the UI language and,
        /// for the hide tile, the state).
        /// </summary>
        private void Refresh()
        {
            var state = _controller.State;

            UpdateEngaged();

            _clickThrough.Classes.Set("ringed", state.ClickThrough);
            SetLabel(_clickThrough, "Draw_ClickThrough", DrawHotkeys.ClickThrough);

            // lit while hidden, as in the mockup; the tip names what a press will do
            _hide.Icon = (Geometry)Application.Current.FindResource(state.Hidden ? "IconEyeOff" : "IconEye");
            _hide.IsActive = state.Hidden;
            SetLabel(_hide, state.Hidden ? "Draw_ShowInk" : "Draw_HideInk", DrawHotkeys.Hide);

            _keys.IsActive = state.ShowKeys;
            SetLabel(_keys, state.ShowKeys ? "Draw_HideKeys" : "Draw_ShowKeys", DrawHotkeys.ToggleKeys);

            foreach (var (tool, button) in _tools)
            {
                button.IsActive = state.IsToolLit(tool);
                SetLabel(button, DrawPalette.ToolKey(tool), DrawHotkeys.Tool(tool));
            }

            for (var i = 0; i < _swatches.Count; i++)
            {
                _swatches[i].IsSelected = i == state.ColorIndex;
                SetLabel(_swatches[i], DrawPalette.ColorKeys[i], DrawHotkeys.Color(i));
            }

            var color = DrawPalette.Colors[state.ColorIndex];
            for (var i = 0; i < _sizes.Count; i++)
            {
                _sizes[i].DotColor = color;
                _sizes[i].IsActive = i == state.SizeIndex;
                SetLabel(_sizes[i], DrawPalette.Sizes[i].Key, DrawHotkeys.Size(i));
            }

            _undo.IsEnabled = _controller.CanUndo;
            SetLabel(_undo, "Draw_Undo", DrawHotkeys.Undo);

            _clear.IsEnabled = _controller.CanClear;
            SetLabel(_clear, "Draw_ClearAll", DrawHotkeys.ClearAll);

            // over a frozen screen Esc closes too, and is the key worth naming
            SetLabel(_close, "Draw_Close", state.Frozen ? DrawHotkeys.ClickThrough : DrawHotkeys.Close);
        }

        /// <summary>
        /// The optical offsets of the approved mockup: the emblem's mark is pulled 2 px back toward
        /// the grip, and the first colour pushed 2 px forward, so the round logo and the first swatch's
        /// selection ring start on one edge; and the mini buttons are spaced at the strip gap rather
        /// than the dots'. All along the strip axis, so they turn with it.
        /// </summary>
        private void ApplyAxisMargins()
        {
            var horizontal = Tray.Orientation == Orientation.Horizontal;
            _emblem.Margin = horizontal ? new Thickness(-2, 0, 2, 0) : new Thickness(0, -2, 0, 2);
            _swatches[0].Margin = horizontal ? new Thickness(2, 0, 0, 0) : new Thickness(0, 2, 0, 0);

            // the secondary lane's spacing is the 2 px between dots; the mini buttons keep the strip's
            // ordinary 4 px gap between them, as in the mockup
            var lead = TrayTokens.Gap - DotSpacing;
            var miniMargin = horizontal ? new Thickness(lead, 0, 0, 0) : new Thickness(0, lead, 0, 0);
            _undo.Margin = miniMargin;
            _clear.Margin = miniMargin;
            _close.Margin = miniMargin;
        }

        private static TrayButton NewMini()
        {
            var button = new TrayButton();
            button.Classes.Add("mini");
            return button;
        }

        /// <summary>Gives a primary-lane tile its key's hint, rippling in strip order, and draws its glyph
        /// a size down to make room for it.</summary>
        private void Hint(TrayButton button, SimpleKeyGesture gesture)
        {
            button.GlyphSize = TrayTokens.KeyHintGlyphSize;
            button.AccessKey = DrawHotkeys.Hint(gesture);
            button.AccessKeyDelay = TrayTokens.KeyHintStagger * _hinted.Count;
            _hinted.Add(button);
        }

        /// <summary>The tile's tooltip, the localised name with its hotkey ("Undo (Ctrl+Z)", as the image
        /// editor writes them), and its accessible name, with the hotkey as its accelerator key.</summary>
        private static void SetLabel(Control control, string key, SimpleKeyGesture gesture)
        {
            var name = Loc.T(key);
            var shortcut = DrawHotkeys.Label(gesture);
            ToolTip.SetTip(control, $"{name} ({shortcut})");
            AutomationProperties.SetName(control, name);
            AutomationProperties.SetAcceleratorKey(control, shortcut);
        }

        /// <summary>The primary lane: a plain stack that takes the strip's axis from the deck.
        /// StackPanel's own Orientation already has the shape <see cref="ITrayOrientable"/> asks for.</summary>
        private sealed class DrawLanePanel : StackPanel, ITrayOrientable
        {
        }
    }
}
