using System;
using System.Globalization;
using System.Windows.Data;
using Clowd.Util;

namespace Clowd.UI.Converters
{
    public class TimeAgoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime time)
            {
                if (time.Date == DateTime.UtcNow.Date)
                    return "Today";

                if (time.Date == DateTime.UtcNow.Date.AddDays(-1))
                    return "Yesterday";

                if (time.Date >= DateTime.UtcNow.Date.AddDays(-7))
                    return "This week";

                if (time == default)
                    return "Unknown";

                var diff = time - DateTime.UtcNow;
                return PrettyTime.Format(diff);
            }

            throw new NotImplementedException("RecentTimeGroupKeyConverter only supports DateTime values");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
