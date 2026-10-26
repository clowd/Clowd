using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload.Accelerate
{
    /// <summary>
    /// Slices a (non-seekable) stream of unknown length into fixed-size chunks for the accelerated
    /// upload protocol: every chunk is exactly the chunk size except the final one, which may be
    /// 1..=chunkSize bytes (a full-size final chunk is legal). The protocol requires the final
    /// chunk to be marked (<c>?final=1</c>) as it is sent, so EOF is detected with a one-byte
    /// lookahead: each fill reads one byte past the chunk boundary, and coming up short means the
    /// current chunk is the last. The surplus byte is carried into the next fill.
    /// </summary>
    internal sealed class UnknownLengthChunker
    {
        private readonly Stream _source;
        private readonly byte[] _buffer;
        private readonly int _chunkSize;
        private byte _pending;
        private bool _hasPending;
        private bool _finished;

        public UnknownLengthChunker(Stream source, long chunkSize)
        {
            if (chunkSize <= 0 || chunkSize > int.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _chunkSize = (int)chunkSize;
            _buffer = new byte[_chunkSize + 1]; // +1 for the EOF lookahead byte
        }

        /// <summary>The current chunk's bytes, from index 0, after <see cref="ReadNextAsync"/>.
        /// Contents are stable until the next call, so a failed PUT can resend the exact bytes.</summary>
        public byte[] Buffer => _buffer;

        /// <summary>
        /// Reads the next chunk into <see cref="Buffer"/> and reports whether it is the final one.
        /// A length of 0 (only possible on the first call, for an empty source) means no valid
        /// chunk exists — the protocol rejects zero-byte chunks. Must not be called after the
        /// final chunk has been returned.
        /// </summary>
        public async Task<(int Length, bool IsFinal)> ReadNextAsync(CancellationToken ct)
        {
            if (_finished)
                throw new InvalidOperationException("The final chunk has already been read.");

            int filled = 0;
            if (_hasPending)
            {
                _buffer[0] = _pending;
                _hasPending = false;
                filled = 1;
            }

            while (filled < _buffer.Length)
            {
                int read = await _source.ReadAsync(_buffer.AsMemory(filled, _buffer.Length - filled), ct);
                if (read == 0)
                    break;
                filled += read;
            }

            // the fill came up short of the lookahead byte: EOF is inside (or exactly at) the
            // chunk boundary, so this chunk — possibly full-size — is the last.
            if (filled <= _chunkSize)
            {
                _finished = true;
                return (filled, true);
            }

            // overfilled by the lookahead byte: a full chunk with more data behind it. The extra
            // byte becomes the first byte of the next fill.
            _pending = _buffer[_chunkSize];
            _hasPending = true;
            return (_chunkSize, false);
        }
    }
}
