using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RT.Util;
using RT.Util.Consoles;

namespace ClowdSvc
{
    /// <summary>
    /// Implements a logger which is an extension to <see cref="ConsoleLogger"/>. Outputs messages to the console, word-wraps
    /// long messages, and can accept user input. This class is thread safe, but is not serializable and can not be passed across app-domains.
    /// </summary>

    public class ConsoleInputLogger : LoggerBase, IDisposable
    {
        /// <summary>
        /// Set this to false to disable the word-wrapping of messages to the
        /// width of the console window.
        /// </summary>
        public bool WordWrap = true;

        /// <summary>
        /// Set this to false to disable user text input word-wrapping
        /// </summary>
        public bool WordWrapInput = true;

        /// <summary>
        /// Setting this to true will remove double spaces and other whitespace.
        /// </summary>
        public bool RemoveExtraneousWhitespace = false;

        /// <summary>
        /// Set this to false to ensure that all messages are printed to StdOut
        /// (aka Console.Out). By default error messages will be printed to
        /// StdErr instead (aka Console.Error).
        /// </summary>
        public bool ErrorsToStdErr = true;

        protected object _logLock = new object();

        public ConsoleColor InputColor
        {
            get { return _inputColor; }
            set
            {
                PreWriteToConsole();
                _inputColor = value;
                PostWriteToConsole();
            }
        }

        /// <summary>
        /// Constructs a new console logger and begin accepting user input.
        /// </summary>
        public ConsoleInputLogger()
        {
            BeginConsoleReadThread();
        }

        /// <summary>Set this to true to interpret all the messages as EggsML.</summary>
        public bool InterpretMessagesAsEggsML = false;

        /// <summary>Set this to change the input prefix in console.</summary>
        public string ConsoleInputIndicator = "->";

        public EventHandler<ConsoleInputLoggerEventArgs> InputRecieved = delegate { };

        /// <summary>
        /// Gets a text color for each of the possible message types.
        /// </summary>
        public ConsoleColor GetMessageTypeColor(LogType type)
        {
            switch (type)
            {
                case LogType.Info: return ConsoleColor.Gray;
                case LogType.Warning: return ConsoleColor.Yellow;
                case LogType.Error: return ConsoleColor.Red;
                case LogType.Debug: return ConsoleColor.Green;
                default: return ConsoleColor.Gray;
            }
        }

        /// <summary>Logs a message to the console.</summary>
        public override void Log(uint verbosity, LogType type, string message)
        {
            lock (_logLock)
            {
                if (VerbosityLimit[type] < verbosity)
                    return;

                string fmtInfo, indent;
                GetFormattedStrings(out fmtInfo, out indent, verbosity, type);

                var prevCol = Console.ForegroundColor;
                var col = GetMessageTypeColor(type);

                PreWriteToConsole();
                TextWriter consoleStream = (type == LogType.Error && ErrorsToStdErr) ? Console.Error : Console.Out;
                int wrapWidth = WordWrap ? ConsoleUtil.WrapToWidth() : int.MaxValue;
                Console.ForegroundColor = col;
                if (string.IsNullOrWhiteSpace(message))
                {
                    consoleStream.WriteLine(fmtInfo); // don't completely skip blank messages
                }
                else
                {
                    bool first = true;
                    if (InterpretMessagesAsEggsML)
                        foreach (var line in FromEggsNodeWordWrap(EggsML.Parse(message), wrapWidth - fmtInfo.Length))
                        {
                            consoleStream.Write(first ? fmtInfo : indent);
                            first = false;
                            ConsoleUtil.WriteLine(line, type == LogType.Error && ErrorsToStdErr);
                        }
                    else
                        foreach (var line in WordWrapIn(message, wrapWidth - fmtInfo.Length))
                        {
                            consoleStream.Write(first ? fmtInfo : indent);
                            first = false;
                            consoleStream.WriteLine(line);
                        }
                }
                Console.ForegroundColor = prevCol;
                PostWriteToConsole();
            }
        }

        /// <summary>Creates a visual separation in the log, for example if a new section starts.</summary>
        public override void Separator()
        {
            lock (_logLock)
            {
                PreWriteToConsole();
                Console.Out.WriteLine();
                Console.Out.WriteLine(new string('-', ConsoleUtil.WrapToWidth()));
                Console.Out.WriteLine();
                PostWriteToConsole();
            }
        }

        private StringBuilder buffer = new StringBuilder();
        [NonSerialized]
        private Thread readThread;
        private int readStartLine = 0;
        private bool acceptingInput = false;
        private ConsoleColor _inputColor = ConsoleColor.Gray;

        private void BeginConsoleReadThread()
        {
            if (ConsoleUtil.StdOutState() != ConsoleUtil.ConsoleState.Console)
            {
                base.Error("Console stream redirected.");
                return;
            }
            acceptingInput = true;
            Console.Write(ConsoleInputIndicator);
            readThread = new Thread(new ThreadStart(() =>
            {
                while (true)
                {
                    var c = Console.ReadKey(true);
                    lock (_logLock)
                    {
                        if (c.Key == ConsoleKey.Enter)
                        {
                            string input = buffer.ToString();
                            buffer.Clear();
                            Console.WriteLine();
                            Console.ForegroundColor = InputColor;
                            Console.Write(ConsoleInputIndicator);
                            readStartLine = Console.CursorTop;
                            ConsoleInputReceived(input);
                        }
                        else if (c.Key == ConsoleKey.Backspace)
                        {
                            if (buffer.Length > 0)
                            {
                                string old = buffer.ToString();
                                buffer.Remove(buffer.Length - 1, 1);
                                if (WordWrapInput)
                                {
                                    if (!UpdateWrappingBackwards(old, buffer.ToString()))
                                        Console.Write("\b \b");
                                }
                                else
                                {
                                    if (Console.CursorLeft == 0)
                                    {
                                        //this is for when back-spacing multi-lined inputs and reaching the left end of a line.
                                        var desiredLine = Console.CursorTop - 1;
                                        Console.SetCursorPosition(Console.BufferWidth - 1, desiredLine);
                                        Console.Write(" ");
                                        Console.SetCursorPosition(Console.BufferWidth - 1, desiredLine);
                                    }
                                    else
                                    {
                                        Console.Write("\b \b");
                                    }
                                }
                            }
                        }
                        else if (c.Key == ConsoleKey.Escape)
                        {
                            if (buffer.Length > 0)
                            {
                                PreWriteToConsole();
                                buffer.Clear();
                                PostWriteToConsole();
                            }
                        }
                        else
                        {
                            if ((int)c.KeyChar >= 32 && (int)c.KeyChar <= 126)
                            {
                                if (WordWrapInput && (ConsoleUtil.WrapToWidth() - Console.CursorLeft) < 1)
                                {
                                    string old = buffer.ToString();
                                    buffer.Append(c.KeyChar);
                                    UpdateWrappingForward(old, buffer.ToString());
                                }
                                else
                                {
                                    buffer.Append(c.KeyChar);
                                    Console.Write(c.KeyChar);
                                }
                            }
                        }
                    }
                }
            }));
            readThread.Start();
        }
        private void ConsoleInputReceived(string line)
        {
            InputRecieved(this, new ConsoleInputLoggerEventArgs(line));
        }
        private bool UpdateWrappingForward(string oldStr, string newStr)
        {
            if (!WordWrapInput) return false;
            var linesOld = WordWrapIn(ConsoleInputIndicator + oldStr, ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            //var linesOld = new ConsoleColoredString(ConsoleInputIndicator, oldStr).WordWrap(ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            var linesNew = WordWrapIn(ConsoleInputIndicator + newStr, ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            //var linesNew = new ConsoleColoredString(ConsoleInputIndicator, newStr).WordWrap(ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            if (linesNew.Length > linesOld.Length)
            {
                int linefrom = linesOld.Last().ToString().LastIndexOf(' ');
                if (linefrom < ConsoleInputIndicator.Length)
                {
                    Console.WriteLine();
                    Console.Write(linesNew.Last().ToString());
                }
                else
                {
                    Console.SetCursorPosition(linefrom, Console.CursorTop);
                    Console.WriteLine(new string(' ', ConsoleUtil.WrapToWidth() - linefrom));
                    Console.Write(linesNew.Last().ToString());
                }
                return true;
            }
            else
            {
                PreWriteToConsole();
                PostWriteToConsole();
            }
            return false;
        }
        private bool UpdateWrappingBackwards(string oldStr, string newStr)
        {
            if (!WordWrapInput) return false;
            var linesOld = WordWrapIn(ConsoleInputIndicator + oldStr, ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            //var linesOld = new ConsoleColoredString(ConsoleInputIndicator, oldStr).WordWrap(ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            var linesNew = WordWrapIn(ConsoleInputIndicator + newStr, ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            //var linesNew = new ConsoleColoredString(ConsoleInputIndicator, newStr).WordWrap(ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length).ToArray();
            if (linesOld.Length > linesNew.Length)
            {
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', Console.BufferWidth));
                string lastline = linesNew.Last().ToString();
                int linefrom = lastline.LastIndexOf(' ');
                Console.SetCursorPosition(linefrom, Console.CursorTop - 2);
                Console.Write(lastline.Substring(linefrom));
                return true;
            }
            else if (linesOld.Last().Length < linesNew.Last().Length)
            {
                PreWriteToConsole();
                PostWriteToConsole();
            }
            return false;
        }
        private void PreWriteToConsole()
        {
            lock (_logLock)
            {
                if (!acceptingInput) return;
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', Console.BufferWidth));
                Console.SetCursorPosition(0, Console.CursorTop - 1);
                while (Console.CursorTop > readStartLine)
                {
                    Console.SetCursorPosition(0, Console.CursorTop - 1);
                    Console.Write(new string(' ', Console.BufferWidth));
                    Console.SetCursorPosition(0, Console.CursorTop - 1);
                }
            }
        }
        private void PostWriteToConsole()
        {
            lock (_logLock)
            {
                if (!acceptingInput) return;
                Console.ForegroundColor = InputColor;
                readStartLine = Console.CursorTop;
                if (WordWrapInput)
                {
                    if (buffer.Length > 0)
                    {
                        //var lines = new ConsoleColoredString(ConsoleInputIndicator, buffer.ToString()).WordWrap(ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length);
                        //var lines = (ConsoleInputIndicator + buffer.ToString()).WordWrap(ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length);
                        var lines = WordWrapIn(ConsoleInputIndicator + buffer.ToString(), ConsoleUtil.WrapToWidth(), ConsoleInputIndicator.Length);
                        foreach (var line in lines.Take(lines.Count() - 1))
                            Console.WriteLine(line.ToString());
                        Console.Write(lines.Last().ToString());
                    }
                    else
                    {
                        Console.Write(ConsoleInputIndicator);
                    }
                }
                else
                {
                    Console.Write(ConsoleInputIndicator);
                    Console.Write(buffer.ToString());
                }
            }
        }
        private IEnumerable<string> WordWrapIn(string _text, int maxWidth, int hangingIndent = 0)
        {
            if (_text.Length == 0)
                yield break;
            if (maxWidth < 1)
                throw new ArgumentOutOfRangeException("maxWidth", maxWidth, "maxWidth cannot be less than 1");
            if (hangingIndent < 0)
                throw new ArgumentOutOfRangeException("hangingIndent", hangingIndent, "hangingIndent cannot be negative.");

            // Split into "paragraphs"
            foreach (string paragraph in _text.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                // Count the number of spaces at the start of the paragraph
                int indentLen = 0;
                while (indentLen < paragraph.Length && paragraph[indentLen] == ' ')
                    indentLen++;

                var curLine = new List<string>();
                var indent = new string(' ', indentLen + hangingIndent);
                var space = new string(' ', indentLen);

                // Get a list of words
                foreach (var wordForeach in paragraph.Substring(indentLen).Split(new string[] { " " }, RemoveExtraneousWhitespace ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None))
                {
                    var word = wordForeach;
                    if (curLine.Sum(c => c.Length) + space.Length + word.Length > maxWidth)
                    {
                        // Need to wrap
                        if (word.Length + 1 > maxWidth)
                        {
                            // This is a very long word
                            // Leave part of the word on the current line but only if at least 2 chars fit
                            if (curLine.Sum(c => c.Length) + space.Length + 2 <= maxWidth)
                            {
                                int length = maxWidth - curLine.Sum(c => c.Length) - space.Length;
                                curLine.Add(space);
                                curLine.Add(word.Substring(0, length));
                                word = word.Substring(length);
                            }
                            // Commit the current line
                            yield return String.Join("", curLine.ToArray());

                            // Now append full lines' worth of text until we're left with less than a full line
                            while (indent.Length + word.Length > maxWidth)
                            {
                                yield return indent + word.Substring(0, maxWidth - indent.Length);
                                word = word.Substring(maxWidth - indent.Length);
                            }

                            // Start a new line with whatever is left
                            curLine = new List<string>();
                            curLine.Add(indent);
                            curLine.Add(word);
                        }
                        else
                        {
                            // This word is not very long and it doesn't fit so just wrap it to the next line
                            yield return String.Join("", curLine.ToArray());

                            // Start a new line
                            curLine = new List<string>();
                            curLine.Add(indent);
                            curLine.Add(word);
                        }
                    }
                    else
                    {
                        // No need to wrap yet
                        curLine.Add(space);
                        curLine.Add(word);
                    }

                    space = " ";
                }

                yield return String.Join("", curLine.ToArray());
            }
        }
        private static IEnumerable<ConsoleColoredString> FromEggsNodeWordWrap(EggsNode node, int wrapWidth, int hangingIndent = 0)
        {
            var results = new List<ConsoleColoredString> { ConsoleColoredString.Empty };
            EggsML.WordWrap(node, ConsoleColor.Gray, wrapWidth,
                (color, text) => text.Length,
                (color, text, width) => { results[results.Count - 1] += new ConsoleColoredString(text, color); },
                (color, newParagraph, indent) =>
                {
                    var s = newParagraph ? 0 : indent + hangingIndent;
                    results.Add(new ConsoleColoredString(new string(' ', s), color));
                    return s;
                },
                (color, tag, parameter) =>
                {
                    bool curLight = color >= ConsoleColor.DarkGray;
                    switch (tag)
                    {
                        case '~': color = curLight ? ConsoleColor.DarkGray : ConsoleColor.Black; break;
                        case '/': color = curLight ? ConsoleColor.Blue : ConsoleColor.DarkBlue; break;
                        case '$': color = curLight ? ConsoleColor.Green : ConsoleColor.DarkGreen; break;
                        case '&': color = curLight ? ConsoleColor.Cyan : ConsoleColor.DarkCyan; break;
                        case '_': color = curLight ? ConsoleColor.Red : ConsoleColor.DarkRed; break;
                        case '%': color = curLight ? ConsoleColor.Magenta : ConsoleColor.DarkMagenta; break;
                        case '^': color = curLight ? ConsoleColor.Yellow : ConsoleColor.DarkYellow; break;
                        case '=': color = ConsoleColor.DarkGray; break;
                        case '*': color = curLight ? color : (ConsoleColor)((int)color + 8); break;
                    }
                    return Tuple.Create(color, 0);
                });
            if (results.Last().Length == 0)
                results.RemoveAt(results.Count - 1);
            return results;
        }
        public void Dispose()
        {
            readThread.Abort();
            readThread.Join();
        }
    }

    public class ConsoleInputLoggerEventArgs : EventArgs
    {
        public string Input { private set; get; }
        public ConsoleInputLoggerEventArgs(string input)
        {
            Input = input;
        }
    }
}

