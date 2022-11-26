using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Clowd.Localization
{
    public static class Extensions
    {
        public static CultureInfo ToCultureInfo(this LanguageInfo lang)
        {
            return new CultureInfo(lang.CultureName);
        }

        public static void SetAsCurrentCulture(this LanguageInfo lang)
        {
            var culture = String.IsNullOrEmpty(lang.CultureName) ? CultureInfo.DefaultThreadCurrentUICulture : lang.ToCultureInfo();
            Thread.CurrentThread.CurrentUICulture = culture;
            Resources.Strings.SetCulture(culture);
        }
    }
}
