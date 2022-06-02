using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Clowd.Util
{
    public static class PrettyTime
    {
        private static TimeUnit[] _unitMap = new TimeUnit[]
        {
            new TimeUnit(1, "", "", 1000 * 60 * 5, "%u", "moments ago", "right now"),
            new TimeUnit(1, "millisecond", "milliseconds"),
            new TimeUnit(1000, "second", "seconds"),
            new TimeUnit(1000 * 60, "minute", "minutes"),
            new TimeUnit(1000 * 60 * 60, "hour", "hours"),
            new TimeUnit(1000 * 60 * 60 * 24, "", "", 2, "%u", "yesterday", "tomorrow"),
            new TimeUnit(1000 * 60 * 60 * 24, "day", "days"),
            new TimeUnit(1000 * 60 * 60 * 24 * 7, "week", "weeks"),
            new TimeUnit(2629743830L, "month", "months"),
            new TimeUnit(2629743830L * 12L, "year", "years"),
            //new TimeUnit(315569259747L ,"decade", "decades"),
            //new TimeUnit(3155692597470L ,"century", "centuries"),
            //new TimeUnit(31556926000000L ,"millennium", "millennia"),
        };

        public static TimeSpan ParseFriendlyTimeSpan(string friendlySpan)
        {
            var epoch = new DateTime(1970, 1, 1);
            return epoch.AddFriendlyTimeSpan(friendlySpan) - epoch;
        }

        public static string ToFriendlyTimeString(this TimeSpan span)
        {
            string output = "";

            if (span.Days > 0)
                output += span.Days + "d";

            if (span.Hours > 0)
                output += span.Hours + "h";

            if (span.Minutes > 0)
                output += span.Minutes + "M";

            if (span.Seconds > 0)
                output += span.Seconds + "s";

            return output;
        }

        public static DateTime AddFriendlyTimeSpan(this DateTime time, string friendlySpan)
        {
            if (!Regex.IsMatch(friendlySpan, @"^-?(\d+[dwmyMsh])+$"))
                throw new ArgumentException("Invalid time span format", nameof(friendlySpan));

            bool shouldSubtract = false;
            if (friendlySpan.StartsWith("-"))
            {
                friendlySpan = friendlySpan.Substring(1);
                shouldSubtract = true;
            }

            var matches = Regex.Matches(friendlySpan, @"(\d+[dwmyMsh])");
            foreach (Match m in matches)
            {
                var text = m.Value;
                var numLength = text.Length - 1;
                var num = int.Parse(text.Substring(0, numLength));
                if (shouldSubtract)
                    num *= -1;

                var unit = text.Substring(numLength);

                if (unit == "d")
                    time = time.AddDays(num);
                if (unit == "w")
                    time = time.AddDays(num * 7);
                if (unit == "m")
                    time = time.AddMonths(num);
                if (unit == "y")
                    time = time.AddYears(num);
                if (unit == "M")
                    time = time.AddMinutes(num);
                if (unit == "s")
                    time = time.AddSeconds(num);
                if (unit == "h")
                    time = time.AddHours(num);
            }

            return time;
        }

        public static string Format(DateTime date)
        {
            var duration = GetDuration(date, DateTime.Now);
            return duration.Unit.Format(duration);
        }

        public static string Format(TimeSpan time)
        {
            var duration = GetDuration(time);
            return duration.Unit.Format(duration);
        }

        private static Duration GetDuration(TimeSpan ts)
        {
            long difference = Convert.ToInt64(Math.Floor(ts.TotalMilliseconds));
            long absoluteDifference = Math.Abs(difference);

            Duration result = new Duration();
            for (int i = 0; i < _unitMap.Count(); i++)
            {
                TimeUnit unit = _unitMap[i];
                long millisPerUnit = Math.Abs(unit.Milliseconds);
                long quantity = Math.Abs(unit.MaxQuantity);
                bool isLastUnit = (i == _unitMap.Count() - 1);

                if ((quantity == 0) && !isLastUnit)
                {
                    quantity = _unitMap[i + 1].Milliseconds / unit.Milliseconds;
                }

                // does our unit encompass the time duration?
                if ((millisPerUnit * quantity > absoluteDifference) || isLastUnit)
                {
                    result.Unit = unit;
                    if (millisPerUnit > absoluteDifference)
                    {
                        // we are rounding up: get 1 or -1 for past or future
                        result.Quantity = difference < 0 ? -1 : 1;
                    }
                    else
                    {
                        //result.Quantity = (long)Math.Ceiling((double)difference / millisPerUnit);
                        var d = Math.Round(difference / (double)millisPerUnit);
                        result.Quantity = (long)(d);
                    }

                    result.Delta = difference - result.Quantity * millisPerUnit;
                    break;
                }
            }

            return result;
        }

        private static Duration GetDuration(DateTime then, DateTime now)
        {
            TimeSpan ts = then - now;
            return GetDuration(ts);
        }

        internal class Duration
        {
            public long Quantity { get; set; }
            public long Delta { get; set; }
            public TimeUnit Unit { get; set; }
        }

        internal class TimeUnit : IComparable<TimeUnit>
        {
            public long Milliseconds { get; }
            public string Name { get; }
            public string PluralName { get; }
            public long MaxQuantity { get; }
            public string Pattern { get; set; }
            public string PastPrefix { get; set; }
            public string PastSuffix { get; set; }
            public string FutureSuffix { get; set; }
            public string FuturePrefix { get; set; }

            private const int TOLERANCE = 0;
            private const string QUANTITY = "%n";
            private const string UNIT = "%u";

            public TimeUnit(long milliseconds, string name, string pluralName,
                long maxQuantity = 0, string pattern = "%n %u", string pastSuffix = " ago",
                string futureSuffix = " from now")
            {
                Milliseconds = milliseconds;
                Name = name;
                PluralName = pluralName;
                MaxQuantity = maxQuantity;
                Pattern = pattern;
                PastSuffix = pastSuffix;
                FutureSuffix = futureSuffix;
            }

            public int CompareTo(TimeUnit other)
            {
                if (this.Milliseconds < other.Milliseconds)
                    return 1;
                if (this.Milliseconds > other.Milliseconds)
                    return -1;
                return 0;
            }

            public string Format(Duration duration)
            {
                string unit = GetName(duration);
                long quantity = GetQuantity(duration);

                string result = ApplyPattern(unit, quantity);

                if (duration.Quantity < 0)
                    result = PastPrefix + result + PastSuffix;
                else
                    result = FuturePrefix + result + FutureSuffix;

                return result;
            }

            private string GetName(Duration d)
            {
                string result = d.Unit.Name;
                if ((Math.Abs(d.Quantity) == 0) || (Math.Abs(d.Quantity) > 1))
                {
                    result = d.Unit.PluralName;
                }

                return result;
            }

            private long GetQuantity(Duration duration)
            {
                long quantity = Math.Abs(duration.Quantity);
                if (duration.Delta != 0)
                {
                    double threshold = Math.Abs(((double)duration.Delta / (double)duration.Unit.Milliseconds) * 100);
                    if (threshold < TOLERANCE)
                    {
                        quantity = quantity + 1;
                    }
                }

                return quantity;
            }

            private string ApplyPattern(string unit, long quantity)
            {
                string result = Pattern.Replace(QUANTITY, quantity.ToString());
                result = result.Replace(UNIT, unit);
                return result;
            }

            public override string ToString()
            {
                return "Time Unit (" + Name + ")";
            }
        }
    }
}
