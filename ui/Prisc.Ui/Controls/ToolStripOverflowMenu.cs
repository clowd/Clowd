using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>One tool an editor's strip may have to put in its "more" menu. <see cref="Key"/>
    /// is whatever the host persists the tool by, handed straight back when it is picked.</summary>
    public readonly record struct ToolStripOverflowEntry(string Key, ToolButton Button, string Label, KeyGesture Shortcut);

    /// <summary>
    /// The "more" menu both editors open from their tool strip's overflow button when
    /// <see cref="DockAndWrapPanel.HideOverflow"/> is on: a row per tool the strip had to hide,
    /// with the tool's icon, name and shortcut. What a pick means (run the tool, pin it) is the
    /// host's.
    /// </summary>
    public static class ToolStripOverflowMenu
    {
        /// <summary>Opens the menu beside <paramref name="moreButton"/>, listing those of
        /// <paramref name="tools"/> that <paramref name="strip"/> has hidden, in the order given.</summary>
        public static void Show(DockAndWrapPanel strip, Control moreButton,
            IEnumerable<ToolStripOverflowEntry> tools, Action<ToolStripOverflowEntry> picked)
        {
            var items = new List<MenuItem>();

            foreach (var tool in tools)
            {
                if (tool.Button == null || !strip.IsOverflowed(tool.Button))
                    continue;

                var item = new MenuItem
                {
                    Header = tool.Label,
                    InputGesture = tool.Shortcut,
                    // the button's command is still the judge of whether the tool applies right now
                    IsEnabled = tool.Button.IsEffectivelyEnabled,
                };
                item.Icon = BuildIcon(tool.Button.IconPath, item);

                var entry = tool;
                item.Click += (_, _) => picked(entry);
                items.Add(item);
            }

            if (items.Count == 0)
                return;

            var flyout = new MenuFlyout
            {
                Placement = PlacementMode.RightEdgeAlignedTop,
                ItemsSource = items,
            };
            flyout.ShowAt(moreButton);
        }

        /// <summary>The tool's glyph at menu size, inked like the item's text so it follows the
        /// theme and the disabled state.</summary>
        private static Control BuildIcon(Geometry geometry, MenuItem item)
        {
            if (geometry == null)
                return null;

            var path = new Path
            {
                Data = geometry,
                Stretch = Stretch.Uniform,
            };
            path.Bind(Shape.FillProperty, item.GetObservable(TemplatedControl.ForegroundProperty));

            return new Viewbox
            {
                Width = 16,
                Height = 16,
                Child = path,
            };
        }
    }
}
