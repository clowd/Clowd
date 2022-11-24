using System;
using System.Collections.Generic;
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

    }
}
