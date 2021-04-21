using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Clowd.UI.Config
{
    public partial class UploadProviderSettingsEditor : UserControl
    {
        public List<IUploadProvider> Providers
        {
            get { return (List<IUploadProvider>)GetValue(ProvidersProperty); }
            set { SetValue(ProvidersProperty, value); }
        }

        public static readonly DependencyProperty ProvidersProperty
            = DependencyProperty.Register(nameof(Providers), typeof(List<IUploadProvider>), typeof(UploadProviderSettingsEditor), new PropertyMetadata(null, OnProvidersChanged));

        private static void OnProvidersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var oldv = e.OldValue as List<IUploadProvider>;
            if (oldv != null)
            {
                foreach (var o in oldv)
                {
                    o.PropertyChanged -= ProviderPropertyChanged;
                }
            }
            var newv = e.NewValue as List<IUploadProvider>;
            if (newv != null)
            {
                foreach (var n in newv)
                {
                    n.PropertyChanged += ProviderPropertyChanged;
                }
            }
        }

        private static void ProviderPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            App.Current.Settings.SaveQuiet();
        }

        public UploadProviderSettingsEditor()
        {
            InitializeComponent();
        }
    }
}
