using System;
using Avalonia;
using Clowd.UI.Controls;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The rich tip behind each add-track button on the video editor's tool strip: a
    /// <see cref="ToolTipCard"/> whose demo is named by its file stem under Assets/TrackTips, so
    /// the markup in VideoEditorWindow.axaml can say <c>DemoName="video"</c>. The window's
    /// code-behind drives <see cref="ToolTipCard.DisabledReason"/> from the same places it raises
    /// the commands' CanExecute.
    /// </summary>
    public class TrackTip : ToolTipCard
    {
        /// <summary>The demo's file stem under Assets/TrackTips: "video" shows track-video.gif.</summary>
        public static readonly StyledProperty<string> DemoNameProperty =
            AvaloniaProperty.Register<TrackTip, string>(nameof(DemoName));

        public string DemoName
        {
            get => GetValue(DemoNameProperty);
            set => SetValue(DemoNameProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == DemoNameProperty)
            {
                // tools/track-tips/generate.py renders them; tools/track-tips/README.md documents
                // the storyboard, the style rules and how to add a demo for a new tool.
                var name = DemoName;
                DemoSource = String.IsNullOrEmpty(name) ? null : DemoUri("TrackTips", "track-" + name);
            }
        }
    }
}
