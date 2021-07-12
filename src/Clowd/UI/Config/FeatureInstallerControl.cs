using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Installer.Features;
using Clowd.UI.Helpers;
using ModernWpf.Controls;

namespace Clowd.UI.Config
{
    class FeatureInstallerControl : ToggleSwitch
    {
        private readonly IFeature _feature;

        public FeatureInstallerControl(IFeature feature)
        {
            this._feature = feature;
            this.IsOn = _feature.CheckInstalled(App.ExePath);
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            e.Handled = true;
            var asset = App.ExePath;

            if (this.IsOn)
            {
                _feature.Uninstall(asset);
            }
            else
            {
                _feature.Install(asset);
            }

            this.IsOn = _feature.CheckInstalled(asset);
        }
    }
}
