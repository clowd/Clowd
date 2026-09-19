using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A two-part action slot: a full-size main button and a narrow side button beside it (below it in a
    /// column), in one segment-filled rounded box with a hairline between them — the same silhouette as
    /// a <see cref="TraySplitToggle"/> with its chevron, so the two sit in a row without a seam, but with
    /// no state bar and two plain verbs instead of a toggle and a menu.
    /// <para>
    /// Both halves are <see cref="TrayButton"/>s, so the main half takes every <see cref="TrayButtonLook"/>
    /// and the side half is simply a smaller one with a 12 px glyph; nothing is redrawn here. The
    /// control raises <see cref="MainClicked"/> / <see cref="SideClicked"/> and decides nothing itself.
    /// Names and tips are two strings each, exactly as on the toggle: an owner may grow a tip a reason
    /// while the accessible name stays the fixed verb.
    /// </para>
    /// </summary>
    public class TraySplitButton : TemplatedControl, ITrayOrientable
    {
        public static readonly StyledProperty<TrayGlyph> GlyphProperty =
            AvaloniaProperty.Register<TraySplitButton, TrayGlyph>(nameof(Glyph));

        public static readonly StyledProperty<TrayButtonLook> LookProperty =
            AvaloniaProperty.Register<TraySplitButton, TrayButtonLook>(nameof(Look), TrayButtonLook.Normal);

        public static readonly StyledProperty<TrayGlyph> SideGlyphProperty =
            AvaloniaProperty.Register<TraySplitButton, TrayGlyph>(nameof(SideGlyph));

        public static readonly StyledProperty<TrayButtonLook> SideLookProperty =
            AvaloniaProperty.Register<TraySplitButton, TrayButtonLook>(nameof(SideLook), TrayButtonLook.Normal);

        public static readonly StyledProperty<string> MainToolTipProperty =
            AvaloniaProperty.Register<TraySplitButton, string>(nameof(MainToolTip));

        public static readonly StyledProperty<string> MainNameProperty =
            AvaloniaProperty.Register<TraySplitButton, string>(nameof(MainName));

        public static readonly StyledProperty<string> SideToolTipProperty =
            AvaloniaProperty.Register<TraySplitButton, string>(nameof(SideToolTip));

        public static readonly StyledProperty<string> SideNameProperty =
            AvaloniaProperty.Register<TraySplitButton, string>(nameof(SideName));

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TraySplitButton, Orientation>(nameof(Orientation), Orientation.Horizontal);

        private Button _main, _side;

        static TraySplitButton()
        {
            ControlThemes.EnsureRegistered();
        }

        /// <summary>The main half's glyph.</summary>
        public TrayGlyph Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>The main half's look (its glyph colour and hover veil).</summary>
        public TrayButtonLook Look
        {
            get => GetValue(LookProperty);
            set => SetValue(LookProperty, value);
        }

        /// <summary>The side half's glyph, drawn at 12 px.</summary>
        public TrayGlyph SideGlyph
        {
            get => GetValue(SideGlyphProperty);
            set => SetValue(SideGlyphProperty, value);
        }

        /// <summary>The side half's look. Its resting opacity is the theme's (.7, a step behind the
        /// main half) whatever the look says; the look picks the glyph colour and the hover veil.</summary>
        public TrayButtonLook SideLook
        {
            get => GetValue(SideLookProperty);
            set => SetValue(SideLookProperty, value);
        }

        public string MainToolTip
        {
            get => GetValue(MainToolTipProperty);
            set => SetValue(MainToolTipProperty, value);
        }

        /// <summary>Accessible name of the main half.</summary>
        public string MainName
        {
            get => GetValue(MainNameProperty);
            set => SetValue(MainNameProperty, value);
        }

        public string SideToolTip
        {
            get => GetValue(SideToolTipProperty);
            set => SetValue(SideToolTipProperty, value);
        }

        /// <summary>Accessible name of the side half.</summary>
        public string SideName
        {
            get => GetValue(SideNameProperty);
            set => SetValue(SideNameProperty, value);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public event EventHandler MainClicked;

        public event EventHandler SideClicked;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            if (_main != null)
                _main.Click -= OnMainClick;
            if (_side != null)
                _side.Click -= OnSideClick;

            base.OnApplyTemplate(e);

            _main = e.NameScope.Find<Button>("PART_Main");
            _side = e.NameScope.Find<Button>("PART_Side");

            if (_main != null)
                _main.Click += OnMainClick;
            if (_side != null)
                _side.Click += OnSideClick;
        }

        private void OnMainClick(object sender, RoutedEventArgs e)
        {
            MainClicked?.Invoke(this, EventArgs.Empty);
        }

        private void OnSideClick(object sender, RoutedEventArgs e)
        {
            SideClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
