using System.Windows.Input;
using Clowd.Setup.Features;
using ModernWpf.Controls;

namespace Clowd.UI.Config
{
    class FeatureInstallerControl : ToggleSwitch
    {
        private readonly IFeature _feature;

        public FeatureInstallerControl(IFeature feature)
        {
            this._feature = feature;
            this.IsOn = _feature.CheckInstalled(Constants.CurrentExePath);
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            e.Handled = true;
            var asset = Constants.CurrentExePath;

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
