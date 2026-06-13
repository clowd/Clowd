using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Clowd.UI
{
    /// <summary>
    /// Borderless topmost overlay anchored to the bottom-right of the primary screen showing
    /// in-flight uploads (Avalonia take on the original WPF TaskWindow). Owned and populated by
    /// <see cref="TasksViewManager"/>; minimizing hides the window without clearing its items.
    /// </summary>
    public partial class TaskWindow : Window
    {
        private readonly TasksViewManager _manager;

        public TaskWindow()
        {
            InitializeComponent();
        }

        public TaskWindow(TasksViewManager manager) : this()
        {
            _manager = manager;
            DataContext = manager;

            // height follows item count (SizeToContent=Height); keep the bottom edge pinned.
            SizeChanged += (_, _) => PositionBottomRight();
        }

        public void ShowOverlay()
        {
            if (!IsVisible)
                Show();

            PositionBottomRight();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            PositionBottomRight();
        }

        private void PositionBottomRight()
        {
            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen == null)
                return;

            var wa = screen.WorkingArea;
            var width = (int)Math.Round(Bounds.Width * RenderScaling);
            var height = (int)Math.Round(Bounds.Height * RenderScaling);

            Position = new PixelPoint(wa.X + wa.Width - width, wa.Y + wa.Height - height);
        }

        private void MinimizeClicked(object sender, RoutedEventArgs e)
        {
            _manager.MinimizeOverlay();
        }

        private void DismissClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: UploadTaskViewModel vm })
                vm.CancelOrDismiss();
        }

        private async void CopyClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: UploadTaskViewModel vm } && !String.IsNullOrEmpty(vm.Url))
            {
                await ClipboardImpl.SetClipboardText(Clipboard, vm.Url);
                vm.Hide();
            }
        }
    }
}
