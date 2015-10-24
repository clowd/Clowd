using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Screeney.Video
{
    class MonitorUtil
    {
        public static Screen GetScreenContainingRect(Rectangle bounds)
        {
            var point = new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            Screen[] allScreens = Screen.AllScreens;
            for (int i = 0; i < (int)allScreens.Length; i++)
            {
                Screen screen = allScreens[i];
                if (screen.Bounds.Contains(point))
                {
                    return screen;
                }
            }
            return null;
        }
    }
}
