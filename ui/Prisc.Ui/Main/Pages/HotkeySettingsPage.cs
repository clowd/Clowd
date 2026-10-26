using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Prisc.Config;
using Prisc.UI.Config;
using Prisc.UI.Controls;

namespace Prisc.UI.Pages
{
    /// <summary>
    /// The Hotkeys settings page: the actions in titled sections (<see cref="HotkeyPageLayout"/>),
    /// each row the action's name beside its shortcuts (<see cref="HotkeyShortcutsEditor"/>).
    /// Built by hand rather than by <see cref="SettingsControlFactory"/>, because a row edits a
    /// list of shortcuts through <see cref="HotkeyManager"/> rather than binding one property;
    /// the rows still follow their [VisibleWhen] the way generated rows do (the same helpers), so
    /// switching a feature off takes its rows away — and a section whose rows are all gone goes
    /// with them.
    /// </summary>
    /// <remarks>
    /// Nothing here saves settings: every change goes through <see cref="HotkeyEntry.Gestures"/>,
    /// which saves and re-registers on the spot.
    /// </remarks>
    public class HotkeySettingsPage : UserControl
    {
        // the label column sizes to the longest action name, not a preferred width: at the
        // window's default size every pixel it holds back is taken from the gesture buttons.
        private const double FieldGap = 16;
        private const double RowIndent = 16;
        private const double EmptyLabelOpacity = 0.6;

        public HotkeySettingsPage()
        {
            // one panel for every section, so all rows share one label column and the fields form
            // one column down the page; the section titles are full-width blocks between the rows.
            var panel = new SettingsRowsPanel();

            var manager = HotkeyManager.Current;
            var settings = SettingsRoot.Current?.Hotkeys;
            if (manager != null && settings != null)
            {
                AddPlatformNote(panel);
                var properties = TypeDescriptor.GetProperties(settings);
                var first = true;
                foreach (var section in HotkeyPageLayout.Sections)
                {
                    AddSection(panel, section, manager, settings, properties, first);
                    first = false;
                }
            }

            // margin on the content (not ScrollViewer padding), like every other settings page: the
            // right/bottom clearance scrolls with it and the fields never run under the scrollbar.
            panel.Margin = new Thickness(0, 0, 16, 16);
            Content = new FadeEdgeScrollViewer { Content = panel };
        }

        private static void AddPlatformNote(SettingsRowsPanel panel)
        {
            var platform = OperatingSystem.IsMacOS() ? OSPlatform.OSX
                : OperatingSystem.IsWindows() ? OSPlatform.Windows
                : OSPlatform.Linux;
            if (HotkeyPageLayout.PlatformNote(platform) is not { } note)
                return;

            var text = new TextBlock
            {
                Text = note,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 4, 16),
            };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("SemiColorText2"));
            SettingsRowsPanel.SetRole(text, SettingsRowsPanel.Role.Block);
            panel.Children.Add(text);
        }

        private static void AddSection(SettingsRowsPanel panel, HotkeySection section, HotkeyManager manager,
            SettingsHotkey settings, PropertyDescriptorCollection properties, bool first)
        {
            var header = SectionHeader(section.Title);
            header.Margin = new Thickness(0, first ? 0 : 20, 4, 2);
            SettingsRowsPanel.SetRole(header, SettingsRowsPanel.Role.Block);
            panel.Children.Add(header);

            var gates = new List<(object Obj, PropertyDescriptor Pd)>();
            foreach (var row in section.Rows)
            {
                var entry = manager.GetEntry(row.Id);
                var pd = properties[entry.SettingsProperty];
                gates.Add((settings, pd));

                // top-aligned beside the first field and as tall as one, so the name sits on the
                // first shortcut's line however many follow it; the label still wraps (and grows)
                // when the window squeezes the column. Indented under the section title, which
                // stays flush with the divider: the short labels only read as "Draw" / "On Screen"
                // when the nesting is visible.
                var label = new Border
                {
                    MinHeight = 32,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(RowIndent, 8, 0, 8),
                    Child = new TextBlock
                    {
                        Text = row.Label,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                SettingsRowsPanel.SetRole(label, SettingsRowsPanel.Role.Label);

                // the full name, not the row's short label: a screen reader announcing "On Screen
                // shortcut Ctrl+D" has no section title to read it against.
                var editor = new HotkeyShortcutsEditor(entry, pd.DisplayName)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(FieldGap, 8, 4, 8),
                };
                SettingsRowsPanel.SetRole(editor, SettingsRowsPanel.Role.Editor);

                // an action with no shortcut is faded back, so the ones that do something stand
                // out in a page where most rows start empty.
                label.Opacity = editor.IsEmpty ? EmptyLabelOpacity : 1;
                editor.PropertyChanged += (_, e) =>
                {
                    if (e.Property == HotkeyShortcutsEditor.IsEmptyProperty)
                        label.Opacity = editor.IsEmpty ? EmptyLabelOpacity : 1;
                };

                panel.Children.Add(label);
                panel.Children.Add(editor);
                SettingsControlFactory.BindVisibleWhen(label, settings, pd);
                SettingsControlFactory.BindVisibleWhen(editor, settings, pd);
            }

            SettingsControlFactory.GateGroupVisibility(header, gates);
        }

        /// <summary>A section's title over a hairline, in place of the generated pages'
        /// GroupBox: a page of short rows reads as one list, broken up rather than boxed in. The
        /// title is half of every row's name ("Draw" / "On Screen"), so it is set in full-strength
        /// text, not the muted caption color.</summary>
        private static Control SectionHeader(string title)
        {
            var text = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
            };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("SemiColorText0"));

            var rule = new Rectangle { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            rule.Bind(Shape.FillProperty, rule.GetResourceObservable("SemiColorBorder"));

            var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
            stack.Children.Add(text);
            stack.Children.Add(rule);
            return stack;
        }
    }
}
