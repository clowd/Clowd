using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The rich tip behind a tool-strip button in either editor: a header naming the tool (with
    /// its keyboard shortcut as a keycap on the right, when it has one), a sentence or two on what
    /// it does (plus a bullet list of gestures for a tool with many), a short looping demo, and, when the button is disabled, the reason why along the
    /// bottom. Hosted as <c>ToolTip.Tip</c> content inside a
    /// <c>ToolTip</c> wearing the bare <c>RichTipToolTipTheme</c> (AppResources.axaml). The video
    /// editor uses it through <see cref="Clowd.UI.VideoEditor.TrackTip"/>, which resolves the
    /// demo by name; the image editor builds it in code (EditorWindow.CreateToolButton).
    /// </summary>
    public partial class ToolTipCard : UserControl
    {
        public static readonly StyledProperty<string> HeaderProperty =
            AvaloniaProperty.Register<ToolTipCard, string>(nameof(Header));

        public static readonly StyledProperty<string> DescriptionProperty =
            AvaloniaProperty.Register<ToolTipCard, string>(nameof(Description));

        /// <summary>Gestures listed one per row under the description, for a tool with too many to
        /// read as prose; null or empty hides the list.</summary>
        public static readonly StyledProperty<IReadOnlyList<string>> BulletsProperty =
            AvaloniaProperty.Register<ToolTipCard, IReadOnlyList<string>>(nameof(Bullets));

        /// <summary>The keyboard shortcut shown as a keycap beside the header ("R", "Shift+A");
        /// null or empty hides the keycap. The image editor fills it from its tool registry; the
        /// video editor's track tips leave it empty.</summary>
        public static readonly StyledProperty<string> ShortcutProperty =
            AvaloniaProperty.Register<ToolTipCard, string>(nameof(Shortcut));

        /// <summary>The demo GIF's resource uri (see <see cref="DemoUri"/>); null hides the demo frame.</summary>
        public static readonly StyledProperty<Uri> DemoSourceProperty =
            AvaloniaProperty.Register<ToolTipCard, Uri>(nameof(DemoSource));

        /// <summary>Why the button is disabled right now; null or empty hides the footer.</summary>
        public static readonly StyledProperty<string> DisabledReasonProperty =
            AvaloniaProperty.Register<ToolTipCard, string>(nameof(DisabledReason));

        public string Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public string Description
        {
            get => GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public IReadOnlyList<string> Bullets
        {
            get => GetValue(BulletsProperty);
            set => SetValue(BulletsProperty, value);
        }

        public string Shortcut
        {
            get => GetValue(ShortcutProperty);
            set => SetValue(ShortcutProperty, value);
        }

        public Uri DemoSource
        {
            get => GetValue(DemoSourceProperty);
            set => SetValue(DemoSourceProperty, value);
        }

        public string DisabledReason
        {
            get => GetValue(DisabledReasonProperty);
            set => SetValue(DisabledReasonProperty, value);
        }

        public ToolTipCard()
        {
            InitializeComponent();
            Apply();
        }

        /// <summary>
        /// The resource uri of a demo GIF under Assets/: <c>DemoUri("TrackTips", "track-video")</c>
        /// is the video editor's track-video.gif, <c>DemoUri("ToolTips", "tool-pen")</c> the image
        /// editor's tool-pen.gif. The GIFs are generated, not hand-drawn: tools/track-tips and
        /// tools/tool-tips hold the generators and the READMEs documenting the storyboards, the
        /// style rules and how to add a demo for a new tool.
        /// </summary>
        public static Uri DemoUri(string folder, string stem) =>
            new Uri("avares://Clowd.Ui/Assets/" + folder + "/" + stem + ".gif");

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == HeaderProperty || change.Property == DescriptionProperty
                || change.Property == BulletsProperty || change.Property == ShortcutProperty || change.Property == DemoSourceProperty
                || change.Property == DisabledReasonProperty)
                Apply();
        }

        private void Apply()
        {
            if (txtHeader == null)
                return; // properties set before InitializeComponent ran

            txtHeader.Text = Header;
            txtHeader.IsVisible = !String.IsNullOrEmpty(Header);
            var shortcut = Shortcut;
            txtShortcut.Text = shortcut;
            keycap.IsVisible = !String.IsNullOrEmpty(shortcut);
            txtDescription.Text = Description;
            txtDescription.IsVisible = !String.IsNullOrEmpty(Description);

            bulletList.Children.Clear();
            if (Bullets is { } bullets)
            {
                foreach (var bullet in bullets)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    row.Children.Add(BulletText("•", new Thickness(2, 0, 7, 0)));
                    var text = BulletText(bullet, default);
                    text.TextWrapping = TextWrapping.Wrap;
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    bulletList.Children.Add(row);
                }
            }

            bulletList.IsVisible = bulletList.Children.Count > 0;
            // the list continues the description, so the gap between them is the rows' own spacing
            txtDescription.Margin = new Thickness(14, 0, 14, bulletList.IsVisible ? 6 : 10);

            var reason = DisabledReason;
            txtDisabled.Text = reason;
            disabledFooter.IsVisible = !String.IsNullOrEmpty(reason);

            var uri = DemoSource;
            if (demo.Source != uri)
                demo.Source = uri;
            // the border would otherwise keep its margin around a player that measured to nothing
            demoFrame.IsVisible = uri != null && AssetExists(uri);
        }

        // the description's type, so a list reads as part of it
        private static TextBlock BulletText(string text, Thickness margin) => new TextBlock
        {
            Text = text,
            Margin = margin,
            FontSize = 12,
            LineHeight = 17,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xBC)),
        };

        private static bool AssetExists(Uri uri)
        {
            try
            {
                return Avalonia.Platform.AssetLoader.Exists(uri);
            }
            catch
            {
                return false;
            }
        }
    }
}
