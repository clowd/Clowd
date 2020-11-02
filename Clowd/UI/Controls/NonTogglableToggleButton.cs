using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;

namespace Clowd.Controls
{
    public class NonTogglableToggleButton : ToggleButton
    {
        protected override void OnToggle()
        {
            // do nothing
            // base.OnToggle();
        }
    }
}
