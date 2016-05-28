using System;
using System.Linq;
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
            var visibleStates = ((string)parameter).Split(';');
            var taskStatus = value as TaskViewItem.TaskStatus?;
            return taskStatus != null && visibleStates.Contains(taskStatus.Value.ToString()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
