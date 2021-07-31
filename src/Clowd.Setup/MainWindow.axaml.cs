using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Clowd.Setup;
using Clowd.Setup.Features;
using Clowd.Setup.Features.Util;
using Clowd.Setup.Views;

namespace Clowd.Setup
{
    public partial class MainWindow : Window
    {
        public static MainWindow Current { get; private set; }
        public Border ContentView => this.FindControl<Border>("ContentView");

        public MainWindow()
        {
            Current = this;
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif

            var info = ControlPanelInfo.GetInfo(Constants.ClowdAppName, RegistryQuery.CurrentUser);
            ContentView.Child = info == null ? new InstallSplashView() : new UninstallSplashView();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public void SetContent(IControl control)
        {
            ContentView.Child = control;
        }
    }
}
