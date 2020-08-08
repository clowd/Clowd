using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
using Clowd.Utilities;
using Ookii.Dialogs.Wpf;

namespace Clowd.Controls
{
    public partial class FFMpegCodecSettingsEditor3 : UserControl
    {
        public FFmpegSettings CodecSettings
        {
            get { return (FFmpegSettings)GetValue(CodecSettingsProperty); }
            set { SetValue(CodecSettingsProperty, value); }
        }

        public static readonly DependencyProperty CodecSettingsProperty =
            DependencyProperty.Register(nameof(CodecSettings), typeof(FFmpegSettings), typeof(FFMpegCodecSettingsEditor3), new PropertyMetadata(null, CodecChanged));

        private static void CodecChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (FFMpegCodecSettingsEditor3)d;
            var val = e.NewValue as FFmpegSettings;
            if (val != null)
                ths.SelectedCodec = val.GetSelectedPreset();
        }

        public FFmpegCodecPreset SelectedCodec
        {
            get { return (FFmpegCodecPreset)GetValue(SelectedCodecProperty); }
            set { SetValue(SelectedCodecProperty, value); }
        }

        public static readonly DependencyProperty SelectedCodecProperty =
            DependencyProperty.Register(nameof(SelectedCodec), typeof(FFmpegCodecPreset), typeof(FFMpegCodecSettingsEditor3), new PropertyMetadata(null, SelectionChanged));

        private static void SelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (FFMpegCodecSettingsEditor3)d;

            var o_new = e.NewValue as INotifyPropertyChanged;
            var o_old = e.OldValue as INotifyPropertyChanged;

            if (o_new != null)
                o_new.PropertyChanged += ths.CodecPropertyChanged;

            if (o_old != null)
                o_old.PropertyChanged -= ths.CodecPropertyChanged;
        }

        public FFMpegCodecSettingsEditor3()
        {
            InitializeComponent();
        }

        private void CodecPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            App.Current.Settings.SaveQuiet();
        }

        private void Delete_Clicked(object sender, RoutedEventArgs e)
        {
            var selected = SelectedCodec;
            SelectedCodec = CodecSettings.SavedPresets.Single(p => p.GetType() == typeof(FFmpegCodecPreset_libx264));
            CodecSettings.SavedPresets.Remove(selected);
        }

        Window wnd;
        private void Advanced_Click(object sender, RoutedEventArgs e)
        {
            if (wnd != null)
            {
                wnd.Close();
                wnd = null;
            }

            var settings = SelectedCodec;

            bool hasChanged = false;
            bool hasSaved = false;

            var dock = new DockPanel();
            dock.Width = 450;
            dock.Height = 600;
            dock.LastChildFill = true;

            var panel = new DockPanel();
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Background = new SolidColorBrush(Color.FromRgb(223, 227, 232));
            DockPanel.SetDock(panel, Dock.Bottom);
            dock.Children.Add(panel);

            var save = new Button();
            save.Content = "Save";
            save.Padding = new Thickness(15, 7, 15, 7);
            save.Margin = new Thickness(5);
            save.IsEnabled = settings.IsCustom;
            panel.Children.Add(save);
            DockPanel.SetDock(save, Dock.Right);

            var saveAs = new Button();
            saveAs.Content = "Save As Copy";
            saveAs.Padding = new Thickness(15, 7, 15, 7);
            saveAs.Margin = new Thickness(5);
            panel.Children.Add(saveAs);
            DockPanel.SetDock(saveAs, Dock.Right);

            var rectangle = new Rectangle();
            panel.Children.Add(rectangle);

            var datagrid = new PropertyTools.Wpf.DataGrid();
            var collection = new TrulyObservableCollection<FFmpegCliOption>();
            collection.AddRange(settings.GetOptions());
            datagrid.ItemsSource = collection;
            datagrid.AutoInsert = false;
            datagrid.EasyInsert = false;
            collection.PropertyChanged += (a, b) =>
            {
                hasChanged = true;
                hasSaved = false;
            };
            dock.Children.Add(datagrid);

            save.Click += (s, ev) =>
            {
                settings.SetOptions(datagrid.ItemsSource.Cast<FFmpegCliOption>().ToList());
                hasSaved = true;
                wnd.Close();
                App.Current.Settings.SaveQuiet();
            };

            saveAs.Click += (s, ev) =>
            {
                var newSettings = new FFmpegCodecPreset_Custom(settings.Name + " - Copy", settings.Name, settings.Extension);
                newSettings.SetOptions(datagrid.ItemsSource.Cast<FFmpegCliOption>().ToList());
                CodecSettings.SavedPresets.Add(newSettings);
                SelectedCodec = newSettings;
                hasSaved = true;
                wnd.Close();
                App.Current.Settings.SaveQuiet();
            };

            wnd = TemplatedWindow.CreateWindow("Edit Advanced Codec Settings", dock);
            wnd.Closing += (s, ev) =>
            {
                if (hasChanged && !hasSaved)
                {
                    if (this.ShowPrompt(
                        MessageBoxIcon.Warning,
                        "You have made changes to the codec settings. Would you like to go back and save them, or exit and discard your changes?",
                        "Exit without saving?",
                        "Cancel",
                        "Exit Without Saving"))
                    {
                        ev.Cancel = true;
                    }
                }
            };
            wnd.Show();
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CodecSettings.SelectedId = SelectedCodec?.Id;
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.ToString());
        }
    }
}
