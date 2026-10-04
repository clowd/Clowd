using System;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The rich tip behind a tool-strip button in either editor: a header naming the tool (with
    /// its keyboard shortcut as a keycap on the right, when it has one), a sentence or two on what
    /// it does, a short looping demo, and, when the button is disabled, the reason why along the
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
                || change.Property == ShortcutProperty || change.Property == DemoSourceProperty
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

            var reason = DisabledReason;
            txtDisabled.Text = reason;
            disabledFooter.IsVisible = !String.IsNullOrEmpty(reason);

            var uri = DemoSource;
            if (demo.Source != uri)
                demo.Source = uri;
            // the border would otherwise keep its margin around a player that measured to nothing
            demoFrame.IsVisible = uri != null && AssetExists(uri);
        }

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
