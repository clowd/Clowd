using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Clowd.Config;
using Clowd.UI.Config;
using ModernWpf.Controls;
using ModernWpf.Media.Animation;

namespace Clowd.UI
{
    public partial class MainWindow : SystemThemedWindow
    {
        private Dictionary<string, FrameworkElement> _tagCache = new Dictionary<string, FrameworkElement>();

        public MainWindow()
        {
            InitializeComponent();

            // set DrillIn page transition
            var trans = new NavigationThemeTransition();
            trans.DefaultNavigationTransitionInfo = new DrillInNavigationTransitionInfo();
            var col = new TransitionCollection();
            col.Add(trans);
            ContentFrame.ContentTransitions = col;
        }

        private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var selectedItem = (NavigationViewItem)args.SelectedItem;
            sender.Header = selectedItem.Content as string;

            var tag = selectedItem.Tag as string;
            if (tag == null)
                return;

            if (_tagCache.TryGetValue(tag, out var panel))
            {
                ContentFrame.Navigate(panel);
            }
            else
            {
                var item = GetPanelForTag(tag);
                if (item != null)
                {
                    _tagCache[tag] = item;
                    ContentFrame.Navigate(item);
                }
            }
        }

        private FrameworkElement GetPanelForTag(string tag)
        {
            var typesToCheck = new[]
            {
                "Clowd.UI." + tag,
                "Clowd.UI.Pages." + tag,
                "Clowd.UI." + tag + "Page",
                "Clowd.UI.Pages." + tag + "Page",
            };

            foreach(var t in typesToCheck)
            {
                var type = Type.GetType(t);
                if (type != null && type.IsAssignableTo(typeof(FrameworkElement)))
                    return (FrameworkElement)Activator.CreateInstance(type);
            }

            var settingsPage = typeof(SettingsRoot).GetProperties().FirstOrDefault(f => f.Name == tag);
            if (settingsPage != null)
                return new SettingsControlFactory(this, settingsPage.GetValue(SettingsRoot.Current)).GetSettingsPanel();

            return null;
        }
    }
}
