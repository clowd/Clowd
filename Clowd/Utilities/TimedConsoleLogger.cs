using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    public class TimedConsoleLogger
    {
        private readonly string name;
        private readonly DateTime timeStart;
        private readonly Dictionary<string, (DateTime start, DateTime end)> timeLog;
        private readonly StringBuilder history;

        public TimedConsoleLogger(string name, DateTime timeStart)
        {
            this.name = name;
            this.timeStart = timeStart;
            this.timeLog = new Dictionary<string, (DateTime start, DateTime end)>();
            this.history = new StringBuilder();
            printMsg();
            printMsg(name + " Profile Started");
        }

        public void Log(string component, string message)
        {
            var ms = (int)(DateTime.Now - timeStart).TotalMilliseconds;

            if (timeLog.ContainsKey(component))
            {
                timeLog[component] = (timeLog[component].start, DateTime.Now);
            }
            else
            {
                timeLog.Add(component, (DateTime.Now, DateTime.Now));
            }

            printMsg($"+{ms}ms - [{component}] {message}");
        }

        public int GetMetricTime(string metricName)
        {
            if (!timeLog.ContainsKey(metricName))
                return 0;

            var kvp = timeLog[metricName];
            return (int)(kvp.end - kvp.start).TotalMilliseconds;
        }

        public void PrintSummary()
        {
            printMsg();
            printMsg(name + " Summary:");
            foreach (var key in timeLog.Keys)
                printMsg($"  {key} - {GetMetricTime(key)}ms");
            printMsg();
        }

        public string GetSummary()
        {
            return history.ToString();
        }

        private void printMsg(string message = "")
        {
            Console.WriteLine(message);
            history.AppendLine(message);
        }
    }
}
