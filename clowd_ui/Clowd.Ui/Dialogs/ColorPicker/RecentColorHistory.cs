using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Clowd.Config;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The MRU list of custom colors shown as an extra swatch row in both color pickers, backed by
    /// <see cref="SettingsGeneral.RecentColors"/>. Every open picker listens to <see cref="Changed"/>
    /// so a color committed in one shows up in the others without reopening them.
    /// </summary>
    public static class RecentColorHistory
    {
        /// <summary>Raised after the list changes. Handlers run on whichever thread called
        /// <see cref="Add"/>; both pickers only ever call it from the UI thread.</summary>
        public static event EventHandler Changed;

        public static IReadOnlyList<Color> Colors => Settings?.RecentColors ?? (IReadOnlyList<Color>)Array.Empty<Color>();

        /// <summary>
        /// Records a color as the most recent one. Re-picking a color that is already in the list
        /// moves it to the front rather than duplicating it, and the list is trimmed to
        /// <see cref="SettingsGeneral.MaxRecentColors"/>. Fully transparent picks are ignored —
        /// "no color" is already a permanent entry in the fixed palette.
        /// </summary>
        public static void Add(Color color)
        {
            var settings = Settings;
            if (settings == null || color.A == 0)
                return;

            var list = settings.RecentColors ?? new List<Color>();

            // no-op when it is already the newest entry, so merely reopening a picker and
            // dismissing it does not churn the settings file
            if (list.Count > 0 && list[0] == color)
                return;

            var updated = new List<Color>(SettingsGeneral.MaxRecentColors) { color };
            updated.AddRange(list.Where(c => c != color).Take(SettingsGeneral.MaxRecentColors - 1));

            settings.RecentColors = updated;
            SettingsService.Save(SettingsRoot.Current);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        // null before App startup assigns SettingsRoot.Current (and in tests that never do)
        private static SettingsGeneral Settings => SettingsRoot.Current?.General;
    }
}
