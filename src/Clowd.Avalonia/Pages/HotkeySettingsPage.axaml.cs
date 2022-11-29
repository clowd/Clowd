using Avalonia;
using Avalonia.Controls;
using Clowd.Avalonia.Services;

namespace Clowd.Avalonia.Pages
{
    public partial class HotkeySettingsPage : UserControl
    {
        public HotkeySettingsPage()
        {
            InitializeComponent();

            var hotkeys = AvaloniaLocator.Current.GetService<HotkeyRegistrationService>();
            var items = hotkeys.GetRegistrations();
            HotkeyList.Items = items;
        }
    }
}
