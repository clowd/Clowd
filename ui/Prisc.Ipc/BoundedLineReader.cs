using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Prisc.Ipc
{
    /// <summary>
    /// Reads <c>\n</c>-terminated lines (a trailing <c>\r</c> stripped) from a helper's stdout without
    /// ever holding more than <see cref="MaxLineBytes"/> of one line in memory (HELPER_PROTOCOL.md
    /// §2.1). A longer line is consumed to its end and reported as oversize instead of returned, so a
    /// runaway helper writing native noise with no newline cannot grow the parent without bound.
    /// </summary>
    internal sealed class BoundedLineReader
    {
        /// <summary>The §2.1 cap, counted in UTF-8 bytes like the child side's.</summary>
        public const int MaxLineBytes = 1024 * 1024;

        private readonly TextReader _reader;
        private readonly int _maxLineBytes;
        private readonly char[] _chunk = new char[8192];
        private readonly StringBuilder _line = new();
        private int _start;
        private int _end;
        private int _lineBytes;
        private bool _oversize;
        private bool _eof;

        public BoundedLineReader(TextReader reader, int maxLineBytes = MaxLineBytes)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _maxLineBytes = maxLineBytes;
        }

        /// <summary>The next line, or null at EOF. <c>Oversize</c> is true (and the line null) for a
        /// line over the cap; a final line without a newline is still returned.</summary>
        public async ValueTask<(string Line, bool Oversize)?> ReadLineAsync()
        {
            while (true)
            {
                if (_start == _end)
                {
                    if (_eof)
                        return null;

                    _start = 0;
                    _end = await _reader.ReadAsync(_chunk.AsMemory()).ConfigureAwait(false);
                    if (_end == 0)
                    {
                        _eof = true;
                        // an unterminated last line still counts
                        if (_line.Length > 0 || _oversize)
                            return TakeLine();
                        return null;
                    }
                }

                var newline = Array.IndexOf(_chunk, '\n', _start, _end - _start);
                if (newline < 0)
                {
                    Append(_end - _start);
                    _start = _end;
                    continue;
                }

                Append(newline - _start);
                _start = newline + 1;
                return TakeLine();
            }
        }

        private void Append(int count)
        {
            if (_oversize || count <= 0)
                return;

            _lineBytes += Encoding.UTF8.GetByteCount(_chunk, _start, count);
            if (_lineBytes > _maxLineBytes)
            {
                // drop what we have and everything up to the next newline
                _oversize = true;
                _line.Clear();
                return;
            }

            _line.Append(_chunk, _start, count);
        }

        private (string, bool) TakeLine()
        {
            var oversize = _oversize;
            string line = null;
            if (!oversize)
            {
                if (_line.Length > 0 && _line[^1] == '\r')
                    _line.Length--;
                line = _line.ToString();
            }

            _line.Clear();
            _lineBytes = 0;
            _oversize = false;
            return (line, oversize);
        }
    }
}
