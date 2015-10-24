using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ScreenCaptureFilter : IFeatureInstaller
    {
        private const string CLSID_LegacyAmFilterCategory = "083863F1-70DE-11d0-BD40-00A0C911CE86";
        private const string CLSID_VideoInputDeviceCategory = "860BB310-5D01-11d0-BD3B-00A0C911CE86";
        private const string CLSID_VideoCompressorCategory = "33D9A760-90C8-11d0-BD43-00A0C911CE86";
        private const string CLSID_AudioCompressorCategory = "33D9A761-90C8-11d0-BD43-00A0C911CE86";
        private const string CLSID_AudioInputDeviceCategory = "33D9A762-90C8-11d0-BD43-00A0C911CE86";
        private const string CLSID_AudioRendererCategory = "E0F158E1-CB04-11d0-BD4E-00A0C911CE86";
        private const string CLSID_MidiRendererCategory = "4EFE2452-168A-11d1-BC76-00C04FB9453B";

        public bool CheckInstalled(string assetPath, RegistryQuery context)
        {
            var cat = new Sonic.DSCategory(new Guid(CLSID_VideoInputDeviceCategory));
            foreach (var inputDevice in cat)
            {
                if (inputDevice.Filter != null)
                {
                    if (inputDevice.Filter.Name.Equals("clowd-virtual-camera", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public void Install(string assetPath, InstallMode context)
        {
            throw new NotImplementedException();
        }

        public void Uninstall(string assetPath, RegistryQuery context)
        {
            throw new NotImplementedException();
        }
    }
}
