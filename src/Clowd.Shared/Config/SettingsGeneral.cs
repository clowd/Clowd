using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    public class SettingsGeneral : CategoryBase
    {
        [Browsable(false)]
        public string LastUploadPath
        {
            get => _lastUploadPath;
            set => Set(ref _lastUploadPath, value);
        }

        [Browsable(false)]
        public string LastSavePath
        {
            get => _lastSavePath;
            set => Set(ref _lastSavePath, value);
        }

        [DisplayName("Confirm before exit")]
        [Description("If true, Clowd will prompt for confirmation before closing.")]
        public bool ConfirmClose
        {
            get => _confirmClose;
            set => Set(ref _confirmClose, value);
        }

        private string _lastUploadPath;
        private string _lastSavePath;
        private bool _confirmClose = true;
    }
}
