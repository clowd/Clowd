using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.Localization;
using Clowd.UI.Controls.Tray;
using Clowd.UI.Helpers;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// The draw-on-screen toolbar: a two-lane strip behind a full-height grip. The primary lane is
    /// the emblem, the two modes (click-through, hide) and the seven tools at full size; the compact
    /// secondary lane is the colours, the sizes and the ways out (undo, clear, close).
    /// <para>
    /// It owns no state. Everything it shows is read off <see cref="IDrawOnScreenController"/> in
    /// <see cref="Refresh"/>, which runs on every <see cref="IDrawOnScreenController.Changed"/>, and
    /// every click is a controller command — so the session stays the one writer of the mode and the
    /// toolbar can never show a state the canvases are not in. Like the other strips it never takes
    /// keyboard focus (the chassis is WS_EX_NOACTIVATE). It is the only draw-on-screen window kept
    /// out of screen captures: the ink must stay in the picture, the toolbar must not.
    /// </para>
    /// </summary>
    public sealed class DrawOnScreenToolbar : FloatingTrayWindow
    {
        // the mockup's `.tb.mini svg.glyph`: the stroked close X is drawn a size up from the filled mini
        // icons and with a 2-unit stroke, so it carries the same weight as Undo/Delete beside it.
        // Kept out of TrayGlyphs.All on purpose (TrayGlyphsTests pins that set and its 1.8 stroke).
        private const double MiniStrokeGlyphSize = 16;
        private static readonly TrayGlyph MiniX = new TrayGlyph("x-mini", TrayGlyphs.X.StrokePaths.ToArray(), null, 2.0);

        // the mockup's gap between neighbouring colour and size tiles (its `.seg { gap: 2px }`)
        private const double DotSpacing = 2;

        private readonly IDrawOnScreenController _controller;

        private readonly TrayEmblem _emblem;
        private readonly TrayButton _clickThrough;
        private readonly TrayButton _hide;
        private readonly List<(ToolType Tool, TrayButton Button)> _tools = new();
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
            })
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));

            // ---- primary lane: emblem · click-through · hide | tools ----
            // the emblem lives in the lane rather than on the chassis (HasEmblem is off) so it opens
            // the first row instead of standing beside both. Its margin is set per axis, see
            // ApplyAxisMargins.
            _emblem = new TrayEmblem();

            // click-through is a mode the strip shows with a ring rather than the accent fill: the
            // fill belongs to the lit tool, and both can be read at once (see Refresh).
            _clickThrough = new TrayButton
            {
                Icon = (Geometry)Application.Current.FindResource("IconToolPointerFilled"),
            };
            // the mockup's 1.5 px optical shift for the pointer's lopsided silhouette
            _clickThrough.Classes.Add("nudge");
            _clickThrough.Click += (s, e) => _controller.ToggleClickThrough();

            _hide = new TrayButton();
            _hide.Click += (s, e) => _controller.ToggleHide();

            var primary = new DrawLanePanel { Spacing = TrayTokens.Gap };
            primary.Children.Add(_emblem);
            primary.Children.Add(_clickThrough);
            primary.Children.Add(_hide);
            primary.Children.Add(new TrayDivider());

            foreach (var tool in DrawPalette.Tools)
            {
                var button = new TrayButton
                {
                    Icon = (Geometry)Application.Current.FindResource(DrawPalette.ToolIconKey(tool)),
                };
                button.Click += (s, e) => _controller.PickTool(tool);
                _tools.Add((tool, button));
                primary.Children.Add(button);
            }

            // ---- secondary lane: colours | sizes | undo · clear · close ----
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

            foreach (var way in new[] { _undo, _clear, _close })
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

            _controller.Changed += OnControllerChanged;
            Loc.CultureChanged += OnCultureChanged;
            Refresh();

            // subscribed after the chassis's own Opened handler (registered in the base constructor),
            // so the native window is fully set up before its display affinity is changed.
            Opened += (s, e) => WindowNativeExtensions.ExcludeFromScreenCapture(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            _controller.Changed -= OnControllerChanged;
            Loc.CultureChanged -= OnCultureChanged;
            base.OnClosed(e);
        }

        private void OnControllerChanged(object sender, EventArgs e) => Refresh();

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

            _clickThrough.Classes.Set("ringed", state.ClickThrough);
            SetLabel(_clickThrough, "Draw_ClickThrough");

            // lit while hidden, as in the mockup; the tip names what a press will do
            _hide.Icon = (Geometry)Application.Current.FindResource(state.Hidden ? "IconEyeOff" : "IconEye");
            _hide.IsActive = state.Hidden;
            SetLabel(_hide, state.Hidden ? "Draw_ShowInk" : "Draw_HideInk");

            foreach (var (tool, button) in _tools)
            {
                button.IsActive = state.IsToolLit(tool);
                SetLabel(button, DrawPalette.ToolKey(tool));
            }

            for (var i = 0; i < _swatches.Count; i++)
            {
                _swatches[i].IsSelected = i == state.ColorIndex;
                SetLabel(_swatches[i], DrawPalette.ColorKeys[i]);
            }

            var color = DrawPalette.Colors[state.ColorIndex];
            for (var i = 0; i < _sizes.Count; i++)
            {
                _sizes[i].DotColor = color;
                _sizes[i].IsActive = i == state.SizeIndex;
                SetLabel(_sizes[i], DrawPalette.Sizes[i].Key);
            }

            _undo.IsEnabled = _controller.CanUndo;
            SetLabel(_undo, "Draw_Undo");

            _clear.IsEnabled = _controller.CanClear;
            SetLabel(_clear, "Draw_ClearAll");

            SetLabel(_close, "Draw_Close");
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
            _clear.Margin = miniMargin;
            _close.Margin = miniMargin;
        }

        private static TrayButton NewMini()
        {
            var button = new TrayButton();
            button.Classes.Add("mini");
            return button;
        }

        /// <summary>The tile's tooltip and accessible name: the localised name only, never a shortcut.</summary>
        private static void SetLabel(Control control, string key)
        {
            var text = Loc.T(key);
            ToolTip.SetTip(control, text);
            AutomationProperties.SetName(control, text);
        }

        /// <summary>The primary lane: a plain stack that takes the strip's axis from the deck.
        /// StackPanel's own Orientation already has the shape <see cref="ITrayOrientable"/> asks for.</summary>
        private sealed class DrawLanePanel : StackPanel, ITrayOrientable
        {
        }
    }
}
