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

        public TimedConsoleLogger(string name, DateTime timeStart)
        {
            this.name = name;
            this.timeStart = timeStart;
            this.timeLog = new Dictionary<string, (DateTime start, DateTime end)>();
            Console.WriteLine();
            Console.WriteLine(name + " Profile Started");
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


            Console.WriteLine($"+{ms}ms - [{component}] {message}");
        }

        public void PrintSummary()
        {
            Console.WriteLine();
            Console.WriteLine(name + " Summary:");
            foreach (var kvp in timeLog)
                Console.WriteLine($"  {kvp.Key} - {(int)(kvp.Value.end - kvp.Value.start).TotalMilliseconds}ms");
            Console.WriteLine();
        }
    }
}
