using System;
using System.Globalization;
using System.Threading;

namespace Clowd.Localization
{
    public record struct LanguageInfo
    {
        public string Name { get; set; }

        public string CultureName { get; set; }

        public LanguageInfo()
        {
            Name = "System Default";
        }

        public LanguageInfo(string name, string cultureName)
        {
            Name = name;
            CultureName = cultureName;
        }

        public static LanguageInfo GetDefault()
        {
            return new LanguageInfo();
        }
    }
}
