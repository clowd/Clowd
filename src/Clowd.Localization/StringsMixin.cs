using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using ReswPlusLib;

namespace Clowd.Localization.Resources
{
    partial class Strings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public static Strings Instance => _instance ?? (_instance = new Strings());
        private static Strings _instance;

        private Strings() { }

        public void SetCulture(CultureInfo culture)
        {
            CultureInfo = culture;
            OnPropertyChanged(nameof(CultureInfo));
            OnPropertyChanged("Item");
            OnPropertyChanged("Item[]");

            //foreach (var prop in this.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            //{
            //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop.Name));
            //}

            Strings.Instance.GetPlural(nameof(Strings.SettingsNav_RecentSessions), 5d);
        }

        public string GetString(string name) => ResourceManager.GetString(name, CultureInfo);

        public string GetPlural(string name, double value) => ResourceManager.GetPlural(name, value);

        public string this[string key] => GetString(key);

        //public PluralString this[double value] => new PluralString(this, value);

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public class PluralString
        {
            public Strings Parent { get; }

            public double Value { get; }

            public PluralString(Strings parent, double value)
            {
                Parent = parent;
                Value = value;
            }

            public string this[string key] => ResourceManager.GetPlural(key, Value);
        }
    }
}
