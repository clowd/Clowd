using System;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Resources;
using System.Threading;

namespace Clowd.Localization
{
    public static partial class Strings
    {
        private static ResourceManager _resourceManager;
        private static CultureInfo _culture;
        private static ReplaySubject<CultureInfo> _cultureSubject;
        private static IPluralProvider _pluralProvider;

        static Strings()
        {
            _resourceManager = new ResourceManager("Clowd.Localization.Resources.Strings", typeof(Strings).Assembly);
            _cultureSubject = new ReplaySubject<CultureInfo>(1);
            _cultureSubject.Subscribe(v =>
            {
                _culture = v;
                _pluralProvider = PluralHelper.GetPluralChooser(v.TwoLetterISOLanguageName);
                Thread.CurrentThread.CurrentUICulture = v;
                CultureInfo.DefaultThreadCurrentUICulture = v;
            });

            _cultureSubject.OnNext(Languages.GetDefaultUiCulture());
        }

        internal static void SetCulture(CultureInfo culture) => _cultureSubject.OnNext(culture);

        public static IObservable<CultureInfo> GetCultureChangedObservable() => _cultureSubject;

        public static string GetString(StringsKeys resourceKey) => GetString(resourceKey.ToString());

        public static string GetString(string resourceKey) => _resourceManager.GetString(resourceKey, _culture);

        public static string GetPlural(StringsPluralKeys resourceKey, double value) => GetPlural(resourceKey.ToString(), value);

        public static string GetPlural(string resourceKey, double value) => GetPluralInternal(resourceKey, value);

        private static string GetPluralInternal(string key, double number)
        {
            string text = null;
            try
            {
                PluralTypeEnum pluralTypeEnum = _pluralProvider.ComputePlural(number);
                switch (pluralTypeEnum)
                {
                    case PluralTypeEnum.ZERO:
                        text = GetString(key + "_Zero");
                        break;
                    case PluralTypeEnum.ONE:
                        text = GetString(key + "_One");
                        break;
                    case PluralTypeEnum.OTHER:
                        text = GetString(key + "_Other");
                        break;
                    case PluralTypeEnum.TWO:
                        text = GetString(key + "_Two");
                        break;
                    case PluralTypeEnum.FEW:
                        text = GetString(key + "_Few");
                        break;
                    case PluralTypeEnum.MANY:
                        text = GetString(key + "_Many");
                        break;
                }
            }
            catch { }

            try
            {
                if (String.IsNullOrEmpty(text))
                    text = GetString(key + "_Other");

                if (String.IsNullOrEmpty(text))
                    text = GetString(key);
            }
            catch { }

            return String.Format(text ?? "", number);
        }
    }
}
