using System;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Merges the Controls/*.axaml ControlTheme dictionaries into the application resources the
    /// first time any of the templated controls in this folder is used. This keeps the control
    /// themes self-contained (no App.axaml edits required for them to resolve).
    /// </summary>
    internal static class ControlThemes
    {
        private static bool _registered;

        public static void EnsureRegistered()
        {
            if (_registered)
                return;

            var app = Application.Current;
            if (app == null)
                return;

            _registered = true;

            Add(app, "ToolButton");
            Add(app, "RowIconButton");
            Add(app, "CompactSpinner");
            Add(app, "ThemedSpinner");
            Add(app, "CompactDropDown");
            Add(app, "ThemedDropDown");
            Add(app, "CaptionedCheckBox");

            // The floating tray's controls. A sub-folder is just part of the name, since Add() builds the
            // avares URI from it. TrayPopups.axaml is deliberately absent: it is a window-scoped Styles
            // file (the dark tooltip/menu look must not leak into the rest of the app), not a dictionary.
            Add(app, "Tray/FloatingTray");
            Add(app, "Tray/TrayButton");
            Add(app, "Tray/TrayFpsButton");
            Add(app, "Tray/TrayPrimaryButton");
            Add(app, "Tray/TraySplitToggle");
            Add(app, "Tray/TraySplitButton");
            Add(app, "Tray/TrayGrip");
            Add(app, "Tray/TrayStatusBlock");
        }

        private static void Add(Application app, string name)
        {
            var uri = new Uri($"avares://Clowd.Ui/Controls/{name}.axaml");
            app.Resources.MergedDictionaries.Add(new ResourceInclude(uri) { Source = uri });
        }
    }
}
