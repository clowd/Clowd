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
            Add(app, "CaptureToolButton");
            Add(app, "SpinnerTextBox");
            Add(app, "DropDownButton");
            Add(app, "CaptionedCheckBox");
        }

        private static void Add(Application app, string name)
        {
            var uri = new Uri($"avares://Clowd.Ui/Controls/{name}.axaml");
            app.Resources.MergedDictionaries.Add(new ResourceInclude(uri) { Source = uri });
        }
    }
}
