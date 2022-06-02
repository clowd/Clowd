using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Config
{
    public enum TimeOptionUnit
    {
        Seconds = 1,
        Minutes = 2,
        Hours = 3,
        Days = 4,
        Weeks = 5,
        Months = 6,
        Years = 7,
    }

    public class TimeOption : SimpleNotifyObject
    {
        public int Number
        {
            get => _number;
            set => Set(ref _number, value);
        }

        public TimeOptionUnit Unit
        {
            get => _unit;
            set => Set(ref _unit, value);
        }

        public TimeOption()
        { }

        public TimeOption(int number, TimeOptionUnit unit)
        {
            Number = number;
            Unit = unit;
        }

        int _number;
        TimeOptionUnit _unit;

        public TimeSpan ToTimeSpan()
        {
            switch (Unit)
            {
                case TimeOptionUnit.Seconds:
                    return TimeSpan.FromSeconds(Number);
                case TimeOptionUnit.Minutes:
                    return TimeSpan.FromMinutes(Number);
                case TimeOptionUnit.Hours:
                    return TimeSpan.FromHours(Number);
                case TimeOptionUnit.Days:
                    return TimeSpan.FromDays(Number);
                case TimeOptionUnit.Weeks:
                    return TimeSpan.FromDays(Number * 7);
                case TimeOptionUnit.Months:
                    return TimeSpan.FromDays(Number * 30);
                case TimeOptionUnit.Years:
                    return TimeSpan.FromDays(Number * 365);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
