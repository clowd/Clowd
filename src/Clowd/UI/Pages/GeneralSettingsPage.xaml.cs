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
using static Clowd.Config.SettingsGeneral;

namespace Clowd.UI.Pages
{
    public partial class GeneralSettingsPage : Page
    {
        public GeneralSettingsPage()
        {
            InitializeComponent();
            grpUpdates.DataContext = grpShell.DataContext = SquirrelUtil.GetUpdateViewModel();
            grpBehavior.DataContext = SettingsRoot.Current.General;
            comboTheme.ItemsSource = Enum.GetValues(typeof(AppTheme)).Cast<AppTheme>();
        }
    }
}
