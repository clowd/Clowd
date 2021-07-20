using PowerArgs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    internal enum LogAutoResponse
    {
        None = 0,
        Yes = 1,
        No = 2,
    }

    internal static class Log
    {
        public static string LogFile { get; set; }

        public static LogAutoResponse AutoResponse { get; set; } = LogAutoResponse.None;

        public static bool IsConsoleMode { get; set; } = false;

        private static bool _initialized = false;

        public static void Red(string message)
        {
            LogToFile(message, "ERRO");
            message.ToRed().WriteLine();
        }

        public static void Yellow(string message)
        {
            LogToFile(message, "WARN");
            message.ToYellow().WriteLine();
        }

        public static void Green(string message)
        {
            LogToFile(message, "INFO");
            message.ToGreen().WriteLine();
        }

        public static void White(string message)
        {
            LogToFile(message, "INFO");
            message.ToWhite().WriteLine();
        }

        public static void Spinner(string message, Action<Kurukuru.Spinner> fn)
        {
            Kurukuru.Spinner.Start(message, (s) =>
            {
                LogToFile(message, "INFO");
                fn(s);
                LogToFile(message + "    Complete.", "INFO");
            });
        }

        //public static bool IsUserSure(string prompt)
        //{
        //    if (SilentMode)
        //    {
        //        LogToFile(prompt, "PMPT");
        //        return true;
        //    }
        //    else
        //    {
        //        LogToFile(prompt + ". Are you sure?", "PMPT");
        //        return Interact.IsUserSure(prompt);
        //    }
        //}

        public static bool YesOrNo(string prompt)
        {
            if (AutoResponse == LogAutoResponse.Yes)
            {
                White(prompt + " (yes: from flags)");
                return true;
            }
            else if (AutoResponse == LogAutoResponse.No)
            {
                White(prompt + " (no: from flags)");
                return false;
            }
            else
            {
                LogToFile(prompt + " (y/n)", "INFO");

                while (true)
                {
                    Console.Write(prompt + " ");
                    "(y/n): ".ToWhite().Write();
                    var response = Console.ReadLine();

                    if (response.Equals("y", StringComparison.OrdinalIgnoreCase) || response.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    {
                        LogToFile(prompt + " (yes: user input)", "INFO");
                        return true;
                    }

                    if (response.Equals("n", StringComparison.OrdinalIgnoreCase) || response.Equals("no", StringComparison.OrdinalIgnoreCase))
                    {
                        LogToFile(prompt + " (no: user input)", "INFO");
                        return false;
                    }

                    $"Unrecognized Option: '{response}', response should be 'y' or 'n'.".ToRed().WriteLine();
                }
            }
        }

        public static void YesOrThrow(string prompt, string failMessage)
        {
            if (!YesOrNo(prompt))
                throw new Exception(failMessage);
        }

        internal static void LogToFile(string message, string tag)
        {
            var date = DateTime.Now;
            if (!String.IsNullOrWhiteSpace(LogFile))
            {
                var path = Path.GetFullPath(LogFile);
                if (Directory.Exists(Path.GetDirectoryName(path)))
                {
                    if (!_initialized)
                    {
                        _initialized = true;
                        if (IsConsoleMode)
                        {
                            // if it's the first time writing to this file, lets print the command line args to this program
                            File.AppendAllText(path, $"{Environment.NewLine}> {Constants.CurrentExePath} {String.Join(" ", Environment.GetCommandLineArgs().Skip(1))}{Environment.NewLine}{Environment.NewLine}");
                        }
                    }

                    File.AppendAllText(path, $"[{date.ToShortDateString()} {date.ToShortTimeString()}][{tag}] {message}{Environment.NewLine}");
                }
            }
        }
    }
}
