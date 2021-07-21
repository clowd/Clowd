using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Clowd.UI.Dialogs.Font
{
    public class FontValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "";

            if (value is FontFamily font)
            {
                //var name = I18NUtil.CurrentLanguage;
                //if (string.IsNullOrWhiteSpace(name))
                //{
                //    name = I18NUtil.GetCurrentLanguage();
                //}
                //var names = new[] { name, I18NUtil.GetLanguage() };
                //foreach (var s in names)
                //{
                //    if (font.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(s), out var localizedName))
                //    {
                //        if (!string.IsNullOrEmpty(localizedName))
                //        {
                //            return localizedName;
                //        }
                //    }
                //}

                return font.Source;
            }
            throw new NotSupportedException();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("ConvertBack not supported");
        }
    }
}
