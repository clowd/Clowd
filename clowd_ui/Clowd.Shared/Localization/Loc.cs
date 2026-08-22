using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using Avalonia.Threading;

namespace Clowd.Localization
{
    /// <summary>
    /// The single entry point for localized text.
    /// <para>
    /// Static members (<see cref="T(string)"/>, <see cref="ApplyCulture"/>, …) are for code; the
    /// <see cref="Current"/> singleton exists only so XAML has an <see cref="INotifyPropertyChanged"/>
    /// binding source — <c>{loc:T Key}</c> binds to <c>Loc.Current["Key"]</c> and re-reads it when
    /// <see cref="ApplyCulture"/> raises the indexer change (see TExtension.cs).
    /// </para>
    /// <para>
    /// Surfaces that cannot be re-bound (the tray <c>NativeMenu</c>, factory-built settings pages)
    /// listen to the static <see cref="CultureChanged"/> event and rebuild themselves instead.
    /// </para>
    /// </summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        /// <summary>WPF's <c>Binding.IndexerName</c>. Avalonia keeps its equivalent internal, so the
        /// literal is repeated here: it is the notification name binding engines watch for when an
        /// indexer's value changes.</summary>
        private const string IndexerName = "Item[]";

        /// <summary>
        /// The UI culture the OS gave the process, captured before anything overrides it — this is
        /// what an empty <c>Language</c> setting means. Reading it here (a static initializer that
        /// runs on the first touch of <see cref="Loc"/>, which is <see cref="ApplyCulture"/> at
        /// startup) is what makes "follow the OS" recoverable after switching away and back.
        /// </summary>
        private static readonly CultureInfo _osCulture = CultureInfo.CurrentUICulture;

        private Loc() { }

        /// <summary>The XAML binding source. Not for use from code — call the static members.</summary>
        public static Loc Current { get; } = new Loc();

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised after <see cref="ApplyCulture"/> actually changes the UI culture, always on
        /// the UI thread, so handlers may rebuild controls directly.</summary>
        public static event EventHandler CultureChanged;

        /// <summary>The culture strings are currently resolved in. Deliberately not named
        /// <c>CurrentCulture</c>: this is the *UI* culture and must never be passed to
        /// <see cref="String.Format(IFormatProvider, string, object[])"/> or a parser, which follow
        /// <see cref="CultureInfo.CurrentCulture"/> (the OS region) instead.</summary>
        public static CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;

        /// <summary>Binding-source indexer: <c>Loc.Current["Tray_Exit"]</c>.</summary>
        public string this[string key] => T(key);

        /// <summary>Resolves <paramref name="key"/> in the current UI culture, falling back through
        /// the resource parent chain to English. Returns the key itself when it is missing entirely,
        /// so a typo shows up on screen instead of blanking the control.</summary>
        public static string T(string key)
        {
            if (String.IsNullOrEmpty(key))
                return key;

            try
            {
                return Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            }
            catch (Exception)
            {
                // a damaged/missing satellite assembly must never take a window down with it.
                return key;
            }
        }

        /// <summary>As <see cref="T(string)"/>, then <see cref="String.Format(IFormatProvider, string, object[])"/>
        /// with the current *formatting* culture (numbers and dates follow the OS region, not the
        /// chosen UI language).</summary>
        public static string T(string key, params object[] args)
        {
            var format = T(key);

            if (args == null || args.Length == 0)
                return format;

            try
            {
                return String.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException)
            {
                // a translation with a bad placeholder shouldn't crash the caller (the unit tests
                // catch these before they ship).
                return format;
            }
        }

        /// <summary>Resolves <paramref name="key"/> only if it exists. Used by the settings-control
        /// factory, where a missing convention key means "fall back to the attribute text".</summary>
        public static bool TryGet(string key, out string value)
        {
            value = null;

            if (String.IsNullOrEmpty(key))
                return false;

            try
            {
                value = Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);
            }
            catch (Exception)
            {
                value = null;
            }

            return value != null;
        }

        /// <summary>
        /// Switches the UI language. <paramref name="cultureName"/> is a culture name ("de",
        /// "fr-CA"); null, empty, malformed, or naming a language this build cannot display means
        /// "follow the OS".
        /// <para>
        /// Only the *UI* culture is changed — <see cref="CultureInfo.CurrentCulture"/> is left
        /// alone, so number, date and currency formatting keeps following the user's OS region as
        /// they configured it. Both the calling thread and the default for threads created from now
        /// on are updated.
        /// </para>
        /// <para>
        /// Callable from any thread. The notification half (<see cref="Current"/>'s indexer change
        /// and <see cref="CultureChanged"/>) always runs on the UI thread — posted there when the
        /// caller is elsewhere — because both are consumed by bindings and control rebuilds.
        /// </para>
        /// </summary>
        public static void ApplyCulture(string cultureName)
        {
            var culture = ResolveCulture(cultureName);

            // CurrentUICulture is thread-static, so "did it actually change?" must not be asked of
            // whichever thread happens to be calling. DefaultThreadCurrentUICulture is the
            // process-wide value this method owns, and is null only before the first call — at
            // which point the OS culture is what every thread still sees.
            var previous = CultureInfo.DefaultThreadCurrentUICulture ?? _osCulture;
            bool changed = !Equals(previous, culture);

            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;

            if (!changed)
                return;

            if (Dispatcher.UIThread.CheckAccess())
                NotifyCultureChanged(culture);
            else
                Dispatcher.UIThread.Post(() => NotifyCultureChanged(culture));
        }

        private static void NotifyCultureChanged(CultureInfo culture)
        {
            // a thread that has ever set its own CurrentUICulture (i.e. one that called
            // ApplyCulture) stops honoring DefaultThreadCurrentUICulture, so the UI thread — the
            // one about to re-resolve every string — is set explicitly rather than assumed.
            CultureInfo.CurrentUICulture = culture;

            Current.RaiseAllChanged();
            CultureChanged?.Invoke(null, EventArgs.Empty);
        }

        private static CultureInfo ResolveCulture(string cultureName)
        {
            if (String.IsNullOrWhiteSpace(cultureName))
                return _osCulture;

            CultureInfo culture;

            try
            {
                culture = CultureInfo.GetCultureInfo(cultureName);
            }
            catch (CultureNotFoundException)
            {
                // not a culture name at all — a hand-edited settings file.
                return _osCulture;
            }

            // a well-formed name is not enough: "fr" stops being displayable the moment
            // Strings.fr.resx leaves the build, and the saved setting outlives it. Anything this
            // build has no resources for means "follow the OS", as SettingsGeneral.Language says.
            return IsDisplayable(culture) ? culture : _osCulture;
        }

        /// <summary>Whether <paramref name="culture"/> resolves to something in
        /// <see cref="GetAvailableLanguages"/>, walking the resource fallback chain the same way
        /// ResourceManager would: "de-DE" is displayable when Strings.de.resx ships.</summary>
        private static bool IsDisplayable(CultureInfo culture)
        {
            var available = GetAvailableLanguages();

            for (var candidate = culture; !String.IsNullOrEmpty(candidate.Name); candidate = candidate.Parent)
            {
                foreach (var language in available)
                {
                    if (String.Equals(language.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private void RaiseAllChanged()
        {
            var handler = PropertyChanged;
            if (handler == null)
                return;

            // "Item" is what Avalonia's INPC accessor watches for the hand-built compiled binding in
            // TExtension; "Item[]" is the conventional indexer notification. Raise both.
            handler(this, new PropertyChangedEventArgs("Item"));
            handler(this, new PropertyChangedEventArgs(IndexerName));
        }

        /// <summary>
        /// The languages this build can actually display: the neutral language compiled into
        /// Clowd.Shared plus every satellite assembly shipped beside the executable. Discovered, not
        /// hardcoded — dropping in a new <c>Strings.xx.resx</c> is all it takes for it to appear.
        /// </summary>
        public static IReadOnlyList<CultureInfo> GetAvailableLanguages()
        {
            var assembly = typeof(Loc).Assembly;
            var found = new List<CultureInfo>();

            var neutral = assembly.GetCustomAttribute<NeutralResourcesLanguageAttribute>();
            if (neutral != null)
            {
                try { found.Add(CultureInfo.GetCultureInfo(neutral.CultureName)); }
                catch (CultureNotFoundException) { }
            }

            var satelliteName = assembly.GetName().Name + ".resources.dll";

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(AppContext.BaseDirectory))
                {
                    if (!File.Exists(Path.Combine(dir, satelliteName)))
                        continue;

                    CultureInfo culture;
                    try
                    {
                        culture = CultureInfo.GetCultureInfo(Path.GetFileName(dir));
                    }
                    catch (CultureNotFoundException)
                    {
                        continue; // some other subdirectory that happens to hold resources.
                    }

                    if (!found.Contains(culture))
                        found.Add(culture);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return found
                .OrderBy(c => c.NativeName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        /// <summary>Every key defined by the neutral resource set, sorted ordinally. ResourceSet
        /// enumeration order is unspecified (it is a hash table walk), so the sort is what makes
        /// this — and the strings.json handed to the capture overlay — reproducible.</summary>
        public static IReadOnlyList<string> GetAllKeys()
        {
            var set = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
            if (set == null)
                return Array.Empty<string>();

            var keys = new List<string>();
            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is string key)
                    keys.Add(key);
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>Resolves a batch of keys in one go. This (with <see cref="ResolveByPrefix"/>) is
        /// the seam used to hand a whole string table to the out-of-process capture overlay, which
        /// has no localization machinery of its own.</summary>
        public static IReadOnlyDictionary<string, string> ResolveAll(IEnumerable<string> keys)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            if (keys == null)
                return result;

            foreach (var key in keys)
            {
                if (!String.IsNullOrEmpty(key) && !result.ContainsKey(key))
                    result[key] = T(key);
            }

            return result;
        }

        /// <summary>Resolves every key starting with <paramref name="prefix"/> (e.g. "Capture_").</summary>
        public static IReadOnlyDictionary<string, string> ResolveByPrefix(string prefix)
        {
            if (String.IsNullOrEmpty(prefix))
                return ResolveAll(GetAllKeys());

            return ResolveAll(GetAllKeys().Where(k => k.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }
}
