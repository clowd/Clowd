using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Clowd.Utilities;

namespace Clowd.Controls
{
    public class FFMpegCodecSettingsEditor : UserControl
    {
        public FFMpegCodecSettings CodecSettings
        {
            get { return (FFMpegCodecSettings)GetValue(CodecSettingsProperty); }
            set { SetValue(CodecSettingsProperty, value); }
        }

        public static readonly DependencyProperty CodecSettingsProperty =
            DependencyProperty.Register(nameof(CodecSettings), typeof(FFMpegCodecSettings), typeof(FFMpegCodecSettingsEditor),
                new PropertyMetadata(null, CodecChangedCallback));

        private static void CodecChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (FFMpegCodecSettingsEditor)d;
            ths.Initialize();
        }

        public FFMpegCodecSettingsEditor(Action save)
        {
            this.save = save;
            Initialize();
        }

        Action save;
        ComboBox presets;
        Window wnd;

        void Initialize()
        {
            if (CodecSettings == null)
                return;

            var panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;

            var header = new TextBlock();
            header.Text = CodecSettings.Description;
            header.Margin = new Thickness(0, 6, 0, 0);
            header.FontSize = header.FontSize - 1;
            header.TextWrapping = TextWrapping.Wrap;
            header.Foreground = Brushes.OrangeRed;
            panel.Children.Add(header);

            var presetLabel = new TextBlock();
            presetLabel.Text = "Codec Preset:";
            presetLabel.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(presetLabel);

            presets = new ComboBox();
            presets.ItemsSource = Enum.GetNames(typeof(FFMpegCodecOptionPreset)).Cast<string>();
            presets.SelectedItem = CodecSettings.Preset.ToString();
            presets.SelectionChanged += Presets_SelectionChanged;
            panel.Children.Add(presets);

            var advButton = new Button();
            advButton.Margin = new Thickness(0, 10, 0, 0);
            advButton.Content = "Advanced Codec Settings";
            advButton.Click += AdvButton_Click;
            panel.Children.Add(advButton);

            var disclaimer = new TextBlock();
            disclaimer.Margin = new Thickness(0, 10, 0, 10);
            disclaimer.FontSize = header.FontSize;
            disclaimer.TextWrapping = TextWrapping.Wrap;
            disclaimer.Foreground = Brushes.DarkGray;

            disclaimer.Inlines.Add(new Run("Disclaimer: ") { FontWeight = FontWeights.Bold });
            disclaimer.Inlines.Add("The h264 encoding is licensed by the ");
            disclaimer.Inlines.Add(new Run("MPEG-LA") { FontWeight = FontWeights.Bold });
            disclaimer.Inlines.Add(". Video encoding algorithms are provided by ");
            disclaimer.Inlines.Add(new Run("FFmpeg") { FontWeight = FontWeights.Bold });
            disclaimer.Inlines.Add(" and the various libraries it contains. ");
            disclaimer.Inlines.Add("FFmpeg is free open source software, made available under the ");
            var gpl = new Hyperlink(new Run("GPLv2 license.")) { NavigateUri = new Uri("http://ffmpeg.org/legal.html") };
            gpl.RequestNavigate += (s, ev) => System.Diagnostics.Process.Start(ev.Uri.ToString());
            disclaimer.Inlines.Add(gpl);
            disclaimer.Inlines.Add(" Under the license terms, ");
            var source = new Hyperlink(new Run("the source code of FFmpeg")) { NavigateUri = new Uri("http://ffmpeg.org/download.html") };
            source.RequestNavigate += (s, ev) => System.Diagnostics.Process.Start(ev.Uri.ToString());
            disclaimer.Inlines.Add(source);
            disclaimer.Inlines.Add(" is available freely for download and modification.");

            panel.Children.Add(disclaimer);

            this.Content = panel;
        }

        private void AdvButton_Click(object sender, RoutedEventArgs e)
        {
            if (wnd != null)
            {
                wnd.Close();
                wnd = null;
            }

            var datagrid = new PropertyTools.Wpf.DataGrid();
            datagrid.ItemsSource = CodecSettings.Options;
            datagrid.Width = 450;
            datagrid.Height = 600;
            datagrid.AutoInsert = false;
            datagrid.EasyInsert = false;
            datagrid.SourceUpdated += (a, b) =>
            {
                presets.SelectedItem = FFMpegCodecOptionPreset.Custom.ToString();
                save();
            };

            wnd = TemplatedWindow.CreateWindow("Edit Advanced Codec Settings", datagrid);
            wnd.Closed += (s, ev) => save();
            wnd.Show();
        }

        private void Presets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var dict = Enum.GetValues(typeof(FFMpegCodecOptionPreset))
               .Cast<FFMpegCodecOptionPreset>()
               .ToDictionary(t => t.ToString(), t => t);

            var selected = dict[e.AddedItems.Cast<string>().Single()];

            if (selected != FFMpegCodecOptionPreset.Custom && wnd != null)
            {
                wnd.Close();
                wnd = null;
            }

            if (selected != CodecSettings.Preset)
            {
                CodecSettings.Preset = selected;
                save();
            }
        }
    }
}
