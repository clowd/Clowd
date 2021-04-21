using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class DirectShowFilterInfo
    {
        public DirectShowFilterType FilterType { get; }
        public string ResourceName { get; }
        public string FilePath => Path.Combine(DShowFilter.InstallDirectory, ResourceName);
        public string FilterName { get; }
        public bool FileExists => File.Exists(FilePath);
        public bool IsInstalled => GetDsDevice() != null;
        public bool IsLatest { get; }

        public DirectShowFilterInfo(DirectShowFilterType type, string name, string file, bool latest)
        {
            FilterType = type;
            ResourceName = file;
            FilterName = name;
            IsLatest = latest;
        }

        internal SafeDsDevice GetDsDevice()
        {
            SafeDsDevice device = null;
            DSCategory cat = new DSCategory(GetDsCategory());
            foreach (var inputDevice in cat)
            {
                if (device == null && inputDevice.Name.Equals(FilterName, StringComparison.OrdinalIgnoreCase))
                    device = new SafeDsDevice(inputDevice.ClassID, inputDevice.DevicePath, inputDevice.IsValid, inputDevice.Name);

                inputDevice.Dispose();
            }
            return device;
        }

        internal Guid GetDsCategory()
        {
            switch (FilterType)
            {
                case DirectShowFilterType.Audio:
                    return new Guid(AMovieSetup.CLSID_AudioInputDeviceCategory);
                case DirectShowFilterType.Video:
                    return new Guid(AMovieSetup.CLSID_VideoInputDeviceCategory);
                default:
                    throw new ArgumentOutOfRangeException(nameof(FilterType));
            }
        }
    }

    public class SafeDsDevice
    {
        public Guid ClassId { get; }
        public string DevicePath { get; }
        public bool IsValid { get; }
        public string FilterName { get; }

        public SafeDsDevice(Guid classId, string devicePath, bool isValid, string name)
        {
            this.ClassId = classId;
            this.DevicePath = devicePath;
            this.IsValid = isValid;
            this.FilterName = name;
        }
    }

    public enum DirectShowFilterType
    {
        Video = 1,
        Audio = 2,
    }
}
