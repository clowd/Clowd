using System;
using Clowd.Ui.Models.Common;

namespace Clowd.Ui.Models.Settings;

public enum TimeOptionUnit
{
    Minutes,
    Hours,
    Days,
    Weeks,
    Months,
    Years,
}

public sealed class TimeOption : ObservableObject
{
    private int _number;
    private TimeOptionUnit _unit;

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
    {
    }

    public TimeOption(int number, TimeOptionUnit unit)
    {
        _number = number;
        _unit = unit;
    }

    public TimeSpan ToTimeSpan() => _unit switch
    {
        TimeOptionUnit.Minutes => TimeSpan.FromMinutes(_number),
        TimeOptionUnit.Hours => TimeSpan.FromHours(_number),
        TimeOptionUnit.Days => TimeSpan.FromDays(_number),
        TimeOptionUnit.Weeks => TimeSpan.FromDays(_number * 7),
        TimeOptionUnit.Months => TimeSpan.FromDays(_number * 30),
        TimeOptionUnit.Years => TimeSpan.FromDays(_number * 365),
        _ => TimeSpan.Zero,
    };
}
