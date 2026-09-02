using System;
using System.Collections.Generic;

namespace Clowd.UI
{
    /// <summary>
    /// A bounded ring buffer of a helper process's output, the other half of the plumbing every
    /// driver in this app duplicates (<see cref="ObsCapturer"/>, <c>ScrollDriver</c>,
    /// <c>Vid2GifRunner</c>, <see cref="ShareRegionDriver"/>). libobs and friends are chatty on
    /// stderr for the life of the process, and an hours-long session must not accumulate output
    /// unboundedly — but the last few hundred lines are also the only thing that makes a bare
    /// "the process exited unexpectedly" diagnosable, so they are worth keeping (CLOWD-Z, CLOWD-C).
    /// <para>
    /// Storage only: this class never looks at what a line says. The caps are constructor arguments
    /// rather than shared constants precisely so the existing drivers can adopt it later without
    /// silently changing their own limits (obs uses 1000 lines / 256 KB, scroll 500 / 128 KB).
    /// </para>
    /// <para>Thread-safe under its own lock: every driver appends from a stdio pump thread and
    /// reads from the UI thread.</para>
    /// </summary>
    public sealed class HelperProcessLog
    {
        private readonly object _lock = new();
        private readonly Queue<string> _log = new();
        private readonly int _maxLines;
        private readonly int _maxChars;
        private int _logChars;

        /// <param name="maxLines">How many lines to keep before the oldest is dropped.</param>
        /// <param name="maxChars">A second cap on the total size, because one pathological line
        /// (a dumped buffer, a stack trace on one line) can be larger than the line budget was ever
        /// meant to hold.</param>
        public HelperProcessLog(int maxLines, int maxChars)
        {
            _maxLines = Math.Max(1, maxLines);
            _maxChars = Math.Max(1, maxChars);
        }

        /// <summary>Records one line, evicting the oldest until both caps are satisfied. Empty
        /// lines are dropped — they carry nothing and would push real output out of the window.</summary>
        public void Append(string line)
        {
            if (String.IsNullOrEmpty(line))
                return;

            lock (_lock)
            {
                _log.Enqueue(line);
                _logChars += line.Length;

                // > 1 remaining guard is unnecessary: the caps are at least 1, so the last line
                // always survives even when it alone exceeds _maxChars.
                while (_log.Count > _maxLines || (_logChars > _maxChars && _log.Count > 1))
                    _logChars -= _log.Dequeue().Length;
            }
        }

        /// <summary>Everything currently buffered, oldest first, for the error-log file a fatal
        /// failure writes. Never null; empty when nothing has been recorded.</summary>
        public string GetLog()
        {
            lock (_lock)
                return String.Join(Environment.NewLine, _log);
        }

        /// <summary>The tail of the buffer, for an error dialog that has room for a handful of
        /// lines: whatever went wrong is always at the end, after the routine startup chatter.
        /// Returns null (not "") when there is nothing at all, so a caller can test it in one
        /// place and skip the whole "details" section of its message.</summary>
        public string GetLogTail(int maxLines)
        {
            if (maxLines <= 0)
                return null;

            lock (_lock)
            {
                var skip = Math.Max(0, _log.Count - maxLines);
                var tail = new List<string>(Math.Min(_log.Count, maxLines));
                foreach (var entry in _log)
                {
                    if (skip-- > 0)
                        continue;
                    tail.Add(entry);
                }

                return tail.Count == 0 ? null : String.Join(Environment.NewLine, tail);
            }
        }

        /// <summary>Stows the buffered output on <paramref name="ex"/> (under
        /// <see cref="SentryConfig.ProcessLogKey"/>) so whichever layer ultimately reports it —
        /// usually a catch several frames up a page — attaches the log to the Sentry event, and
        /// returns the same exception so this can wrap a <c>throw</c> or a
        /// <c>CaptureHandled</c> call. An existing entry is left alone: the innermost handler had
        /// the most context. Returns <paramref name="ex"/> unchanged on failure — some exotic
        /// exception types expose a read-only <see cref="Exception.Data"/>, and losing the log is
        /// never a reason to lose the error.</summary>
        public Exception Attach(Exception ex)
        {
            try
            {
                if (ex != null && !ex.Data.Contains(SentryConfig.ProcessLogKey))
                {
                    var log = GetLog();
                    if (log.Length > 0)
                        ex.Data[SentryConfig.ProcessLogKey] = log;
                }
            }
            catch
            {
                // Exception.Data may be read-only for exotic exception types
            }

            return ex;
        }
    }
}
