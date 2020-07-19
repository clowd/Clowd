using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Clowd
{
    public class InteropWindow : Window
    {
        public IntPtr Handle { get; private set; }

        public InteropWindow()
        {
            this.SourceInitialized += InteropWindow_SourceInitialized;
        }

        private void InteropWindow_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        }
    }
}
