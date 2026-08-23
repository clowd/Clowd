using System;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The rich tip behind each add-track button on the editor's tool strip: a header naming the
    /// track, a sentence or two on what it does, a short looping demo, and, when the button is
    /// disabled, the reason why along the bottom. Hosted as <c>ToolTip.Tip</c> content (see
    /// VideoEditorWindow.axaml); the window's code-behind drives <see cref="DisabledReason"/> from
    /// the same places it raises the commands' CanExecute.
    /// </summary>
    public partial class TrackTip : UserControl
    {
        public static readonly StyledProperty<string> HeaderProperty =
            AvaloniaProperty.Register<TrackTip, string>(nameof(Header));

        public static readonly StyledProperty<string> DescriptionProperty =
            AvaloniaProperty.Register<TrackTip, string>(nameof(Description));

        /// <summary>The demo's file stem under Assets/TrackTips: "video" shows track-video.gif.</summary>
        public static readonly StyledProperty<string> DemoNameProperty =
            AvaloniaProperty.Register<TrackTip, string>(nameof(DemoName));

        /// <summary>Why the button is disabled right now; null or empty hides the footer.</summary>
        public static readonly StyledProperty<string> DisabledReasonProperty =
            AvaloniaProperty.Register<TrackTip, string>(nameof(DisabledReason));

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

        public string DemoName
        {
            get => GetValue(DemoNameProperty);
            set => SetValue(DemoNameProperty, value);
        }

        public string DisabledReason
        {
            get => GetValue(DisabledReasonProperty);
            set => SetValue(DisabledReasonProperty, value);
        }

        public TrackTip()
        {
            InitializeComponent();
            Apply();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == HeaderProperty || change.Property == DescriptionProperty
                || change.Property == DemoNameProperty || change.Property == DisabledReasonProperty)
                Apply();
        }

        private void Apply()
        {
            if (txtHeader == null)
                return; // properties set before InitializeComponent ran

            txtHeader.Text = Header;
            txtHeader.IsVisible = !String.IsNullOrEmpty(Header);
            txtDescription.Text = Description;
            txtDescription.IsVisible = !String.IsNullOrEmpty(Description);

            var reason = DisabledReason;
            txtDisabled.Text = reason;
            disabledFooter.IsVisible = !String.IsNullOrEmpty(reason);

            // The GIFs are generated, not hand-drawn: tools/track-tips/generate.py renders them and
            // tools/track-tips/README.md documents the storyboard, the style rules and how to add
            // a demo for a new tool (add a gif_xxx() there, then a TrackTip with its DemoName here).
            var demoName = DemoName;
            var uri = String.IsNullOrEmpty(demoName)
                ? null
                : new Uri("avares://Clowd.Ui/Assets/TrackTips/track-" + demoName + ".gif");
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
