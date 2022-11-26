using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Xml.Linq;
using ReswPlusLib;
using ReswPlusLib.Interfaces;

namespace Clowd.Localization.Resources
{
    partial class Strings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public static Strings Instance => _instance ?? (_instance = new Strings());
        private static Strings _instance;

        private IPluralProvider _pluralProvider;
        private Dictionary<string, PluralableString> _cache = new();

        private Strings()
        {
            _pluralProvider = CreatePluralProvider("en");
        }

        public void SetCulture(CultureInfo culture)
        {
            _pluralProvider = CreatePluralProvider(culture.TwoLetterISOLanguageName);
            CultureInfo = culture;
            OnPropertyChanged(nameof(CultureInfo));
            OnPropertyChanged("Item");
            OnPropertyChanged("Item[]");


            foreach (var item in _cache.Values)
            {
                item.Invalidate();
            }
        }


        public PluralableString this[string key]
        {
            get
            {
                if (_cache.TryGetValue(key, out var v)) return v;
                var p = new PluralableString(this, key);
                _cache[key] = p;
                return p;
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string GetPluralInternal(string key, double number)
        {
            string getString(string k)
            {
                return ResourceManager.GetString(k, CultureInfo);
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

        public class PluralableString : INotifyPropertyChanged
        {
            public string Key { get; }

            public Strings Parent { get; }

            public PluralableString(Strings parent, string key)
            {
                Parent = parent;
                Key = key;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public void Invalidate()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            }

            public string GetPluralForValue(double value)
            {
                return Parent.GetPluralInternal(Key, value);
            }

            public string this[double value] => Parent.GetPluralInternal(Key, value);

            public override string ToString() => ResourceManager.GetString(Key, Parent.CultureInfo) ?? Parent.GetPluralInternal(Key, 0d);

            public static implicit operator string(PluralableString plural) => plural.ToString();
        }
    }
}
