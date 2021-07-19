using Dragablz;
using System.Windows;

namespace Clowd.UI.Controls
{
    public static class DragablzItemHelper
    {
        #region Icon

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached(
                "Icon",
                typeof(object),
                typeof(DragablzItemHelper));

        public static object GetIcon(DragablzItem item)
        {
            return item.GetValue(IconProperty);
        }

        public static void SetIcon(DragablzItem item, object value)
        {
            item.SetValue(IconProperty, value);
        }

        #endregion
    }
}
