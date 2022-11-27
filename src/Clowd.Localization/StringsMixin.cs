using System;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Resources;
using System.Threading;
using ReswPlusLib;
using ReswPlusLib.Interfaces;

namespace Clowd.Localization.Resources
{
    partial class Strings
    {
        private static CultureInfo _culture;
        private static ReplaySubject<CultureInfo> _cultureSubject;
        private static IPluralProvider _pluralProvider;

        static Strings()
        {
            _cultureSubject = new ReplaySubject<CultureInfo>(1);
            _cultureSubject.Subscribe(v =>
            {
                CultureInfo = v;
                _culture = v;
                _pluralProvider = CreatePluralProvider(v.TwoLetterISOLanguageName);
                Thread.CurrentThread.CurrentUICulture = v;
                CultureInfo.DefaultThreadCurrentUICulture = v;
            });

            _cultureSubject.OnNext(Languages.GetDefaultUiCulture());
        }

        internal static void SetCulture(CultureInfo culture) => _cultureSubject.OnNext(culture);

        public static IObservable<CultureInfo> GetCultureChangedObservable() => _cultureSubject;

        public static string GetString(string resourceKey) => ResourceManager.GetString(resourceKey, _culture);

        public static string GetPlural(string resourceKey, double value) => GetPluralInternal(resourceKey, value);

        private static string GetPluralInternal(string key, double number)
        {
            string getString(string k)
            {
                return ResourceManager.GetString(k, _culture);
            }

            string text = null;
            PluralTypeEnum pluralTypeEnum = _pluralProvider.ComputePlural(number);
            try
            {
                switch (pluralTypeEnum)
                {
                    case PluralTypeEnum.ZERO:
                        text = getString(key + "_Zero");
                        break;
                    case PluralTypeEnum.ONE:
                        text = getString(key + "_One");
                        break;
                    case PluralTypeEnum.OTHER:
                        text = getString(key + "_Other");
                        break;
                    case PluralTypeEnum.TWO:
                        text = getString(key + "_Two");
                        break;
                    case PluralTypeEnum.FEW:
                        text = getString(key + "_Few");
                        break;
                    case PluralTypeEnum.MANY:
                        text = getString(key + "_Many");
                        break;
                }

                if (String.IsNullOrEmpty(text))
                    text = getString(key + "_Other");

                if (String.IsNullOrEmpty(text))
                    text = getString(key);
            }
            catch
            {
            }

            return String.Format(text ?? "", number);
        }

        private static IPluralProvider CreatePluralProvider(string twoLetterIsoCultureName)
        {
            Type t = typeof(ResourceLoaderExtension).Assembly.GetType("ReswPlusLib.Utils.PluralHelper");
            var pluralChooserMth = t.GetMethod("GetPluralChooser", BindingFlags.Static | BindingFlags.Public);
            return pluralChooserMth.Invoke(null, new object[] { twoLetterIsoCultureName }) as IPluralProvider;
        }
    }
}
