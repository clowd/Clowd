using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Clowd.Dialogs
{
    public enum MessageDialogIcon
    {
        None = 0,
        Hand = 0x00000010,
        Question = 0x00000020,
        Exclamation = 0x00000030,
        Asterisk = 0x00000040,
        Stop = Hand,
        Error = Hand,
        Warning = Exclamation,
        Information = Asterisk,
    }

    public partial class MessageDialogWpf : ThemedWindow
    {
        public string MainInstruction
        {
            get { return (string)GetValue(MainInstructionProperty); }
            set { SetValue(MainInstructionProperty, value); }
        }
        public static readonly DependencyProperty MainInstructionProperty =
            DependencyProperty.Register("MainInstruction", typeof(string), typeof(MessageDialogWpf), new PropertyMetadata(""));

        public string BodyText
        {
            get { return (string)GetValue(BodyTextProperty); }
            set { SetValue(BodyTextProperty, value); }
        }

        public static readonly DependencyProperty BodyTextProperty =
            DependencyProperty.Register("BodyText", typeof(string), typeof(MessageDialogWpf), new PropertyMetadata(""));

        public BitmapSource Image
        {
            get { return (BitmapSource)GetValue(ImageProperty); }
            set { SetValue(ImageProperty, value); }
        }

        public static readonly DependencyProperty ImageProperty =
            DependencyProperty.Register("Image", typeof(BitmapSource), typeof(MessageDialogWpf), new PropertyMetadata(null));

        //public MessageDialogIcon Icon
        //{
        //    set
        //    {
        //        SystemIcons.Error
        //    }
        //}



        public MessageDialogWpf()
        {
            //var handle = new WindowInteropHelper(this).EnsureHandle();
            //Clowd.PlatformUtil.Windows.DarkMode.UseImmersiveDarkMode(handle, true);
            //this.Resources.MergedDictionaries.Add(ThemeProvider.DarkTheme);

            InitializeComponent();
            var icon = SystemIcons.Warning;
            Image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, new Int32Rect(0, 0, icon.Width, icon.Height), BitmapSizeOptions.FromEmptyOptions());



            //Clowd.PlatformUtil.Windows.DarkMode.SetDarkModeForWindow(handle, true);
            //var mode = Clowd.PlatformUtil.Windows.DarkMode.IsDarkModeEnabled();
            //Console.WriteLine();
        }
    }
}
