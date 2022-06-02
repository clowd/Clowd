using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.Config;
using Clowd.UI.Config;
using WPFUI.Common;
using WPFUI.Controls;
using static Clowd.Config.SettingsGeneral;

namespace Clowd.UI.Pages
{
    public partial class UploadSettingsPage : Page
    {
        public UploadSettingsPage()
        {
            InitializeComponent();
            this.DataContext = SettingsRoot.Current.Uploads;
        }

        private void ProviderSettingsCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not UploadProviderInfo info) return;

            Func<Window> getWnd = () => Window.GetWindow(this);

            var wnd = new SystemThemedWindow();
            wnd.Owner = getWnd();
            wnd.Title = "Edit settings for " + info.Provider.Name;
            wnd.MaxHeight = 600;
            wnd.SizeToContent = SizeToContent.Height;
            wnd.Width = 500;
            wnd.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(0, GridUnitType.Auto) });

            root.Children.Add(new SettingsControlFactory(getWnd, info).GetSettingsPanel<UserControl>());

            var menu = new Menu();
            menu.HorizontalAlignment = HorizontalAlignment.Right;

            var m_default = new MenuItem();
            var binding = new Binding(nameof(UploadProviderInfo.IsEnabled))
            {
                Mode = BindingMode.TwoWay,
                Source = info,
            };
            m_default.SetBinding(MenuItem.IsEnabledProperty, binding);
            m_default.Header = "Set as default";
            m_default.Icon = SymbolRegular.ChevronCircleDown12;
            menu.Items.Add(m_default);

            if (info.Provider.SupportedUpload.HasFlag(SupportedUploadType.Image))
            {
                var m_image = new MenuItem();
                m_image.Header = "Image";
                m_image.Icon = SymbolRegular.Camera20;
                m_image.Click += (_, _) => SettingsRoot.Current.Uploads.Image = info;
                m_default.Items.Add(m_image);
            }

            if (info.Provider.SupportedUpload.HasFlag(SupportedUploadType.Video))
            {
                var m_video = new MenuItem();
                m_video.Header = "Video";
                m_video.Icon = SymbolRegular.Video24;
                m_video.Click += (_, _) => SettingsRoot.Current.Uploads.Video = info;
                m_default.Items.Add(m_video);
            }

            if (info.Provider.SupportedUpload.HasFlag(SupportedUploadType.Text))
            {
                var m_text = new MenuItem();
                m_text.Header = "Text";
                m_text.Icon = SymbolRegular.Code20;
                m_text.Click += (_, _) => SettingsRoot.Current.Uploads.Text = info;
                m_default.Items.Add(m_text);
            }

            if (info.Provider.SupportedUpload.HasFlag(SupportedUploadType.Binary))
            {
                var m_binary = new MenuItem();
                m_binary.Header = "File";
                m_binary.Icon = SymbolRegular.Document20;
                m_binary.Click += (_, _) => SettingsRoot.Current.Uploads.Binary = info;
                m_default.Items.Add(m_binary);
            }

            var m_test = new MenuItem();
            m_test.Header = "Test";
            m_test.IsEnabled = false;
            m_test.Icon = SymbolRegular.BrainCircuit24;
            menu.Items.Add(m_test);

            var m_close = new MenuItem();
            m_close.Header = "Close";
            m_close.Icon = SymbolRegular.Dismiss20;
            m_close.Click += (_, _) => wnd.Close();
            menu.Items.Add(m_close);

            Grid.SetRow(menu, 1);
            root.Children.Add(menu);

            wnd.Content = root;
            wnd.ShowDialog();
        }
    }
}
