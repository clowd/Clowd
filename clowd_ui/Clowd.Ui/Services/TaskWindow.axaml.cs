using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace Clowd.UI
{
    /// <summary>
    /// Borderless topmost overlay showing in-flight uploads (Avalonia take on the original WPF
    /// TaskWindow), anchored to the bottom-right of the screen the user is working on. The
    /// header is a drag handle; once the user moves the window it stays where they put it
    /// (only the bottom edge is kept pinned as the item list grows/shrinks). Owned and
    /// populated by <see cref="TasksViewManager"/>; minimizing hides the window without
    /// clearing its items.
    /// </summary>
    public partial class TaskWindow : Window
    {
        private readonly TasksViewManager _manager;
        private bool _userMoved;

        public TaskWindow()
        {
            InitializeComponent();
        }

        public TaskWindow(TasksViewManager manager) : this()
        {
            _manager = manager;
            DataContext = manager;

            // height follows item count (SizeToContent=Height); keep the bottom edge pinned.
            SizeChanged += (_, e) =>
            {
                if (_userMoved)
                    PinBottomEdge(e);
                else
                    PositionBottomRight();
            };
        }

        public void ShowOverlay()
        {
            if (!IsVisible)
                Show();

            if (!_userMoved)
                PositionBottomRight();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (!_userMoved)
                PositionBottomRight();
        }

        /// <summary>The screen the user is most likely looking at: the one hosting the active
        /// window, falling back to the primary.</summary>
        private Screen GetTargetScreen()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var active = desktop.Windows.FirstOrDefault(w => w != this && w.IsActive && w.IsVisible);
                if (active != null)
                {
                    var screen = Screens.ScreenFromWindow(active);
                    if (screen != null)
                        return screen;
                }
            }

            return Screens.Primary ?? Screens.All.FirstOrDefault();
        }

        private void PositionBottomRight()
        {
            var screen = GetTargetScreen();
            if (screen == null)
                return;

            var wa = screen.WorkingArea;
            var width = (int)Math.Round(Bounds.Width * RenderScaling);
            var height = (int)Math.Round(Bounds.Height * RenderScaling);

            Position = new PixelPoint(wa.X + wa.Width - width, wa.Y + wa.Height - height);
        }

        /// <summary>After the user has dragged the window, size changes keep the bottom edge
        /// where they left it instead of snapping back to the corner.</summary>
        private void PinBottomEdge(SizeChangedEventArgs e)
        {
            var oldHeight = (int)Math.Round(e.PreviousSize.Height * RenderScaling);
            var newHeight = (int)Math.Round(e.NewSize.Height * RenderScaling);
            if (oldHeight != newHeight)
                Position = new PixelPoint(Position.X, Position.Y + oldHeight - newHeight);
        }

        private void HeaderPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _userMoved = true;
                BeginMoveDrag(e);
            }
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
