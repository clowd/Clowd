using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Dragablz;

namespace Clowd.UI.Controls
{
    public class TabablzControlEx : TabablzControl
    {
        public event EventHandler ItemsChanged;

        static TabablzControlEx()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(TabablzControlEx), new FrameworkPropertyMetadata(typeof(TabablzControlEx)));
        }

        protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);
            ItemsChanged?.Invoke(this, e);
        }
    }
}
