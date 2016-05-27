using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clowd.Converters
{
    [ValueConversion(typeof(TaskViewItem.TaskStatus), typeof(Visibility))]
    public class TaskStatusToVisibilityConverter : IValueConverter
    {
        public static readonly IValueConverter Instance = new TaskStatusToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter.Equals("animation"))
                return value.Equals(TaskViewItem.TaskStatus.InProgress) ? Visibility.Visible : Visibility.Collapsed;
            else if (parameter.Equals("checkmark"))
                return value.Equals(TaskViewItem.TaskStatus.Complete) || value.Equals(TaskViewItem.TaskStatus.Executed) ? Visibility.Visible : Visibility.Collapsed;
            else if (parameter.Equals("cross"))
                return value.Equals(TaskViewItem.TaskStatus.Canceled) || value.Equals(TaskViewItem.TaskStatus.Error) ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
