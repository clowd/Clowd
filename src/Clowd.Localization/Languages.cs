using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Clowd.Localization
{
    public static class Languages
    {
        public static IEnumerable<LanguageInfo> Supported
            => new LanguageInfo[] { LanguageInfo.GetDefault() }.Concat(_languages.Select(k => new LanguageInfo(k.Value, k.Key)));

        public static LanguageInfo GetSystemDefault() => LanguageInfo.GetDefault();

        // https://docwiki.embarcadero.com/RADStudio/Sydney/en/Language_Culture_Names,_Codes,_and_ISO_Values
        static Dictionary<string, string> _languages = new()
        {
            { "en-US", "English" },
            { "fr-FR", "French" },
            { "ru-RU", "Russian" },
        };

        static CultureInfo _defaultCulture;
        static CultureInfo _defaultUiCulture;

        static Languages()
        {
            _defaultCulture = CultureInfo.CurrentCulture;
            _defaultUiCulture = CultureInfo.CurrentUICulture;
        }

        public static CultureInfo GetDefaultUiCulture() => _defaultUiCulture;

        public static void SetLangauge(LanguageInfo info)
        {
            if (String.IsNullOrWhiteSpace(info.CultureName))
            {
                Strings.SetCulture(_defaultUiCulture);
                return;
            }

            try
            {
                var cu = new CultureInfo(info.CultureName);
                Strings.SetCulture(cu);
            }
            catch
            {
                Strings.SetCulture(_defaultUiCulture);
            }
        }
    }
}
