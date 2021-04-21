using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Util
{
    class ProgressStream : Stream
    {

        public EventHandler<BytesReadEventArgs> BytesReadEvent;

        public long BytesRead { get; set; }

        private readonly Stream _baseStream;

        public override bool CanRead => _baseStream.CanRead;

        public override bool CanSeek => _baseStream.CanSeek;

        public override bool CanWrite => _baseStream.CanWrite;

        public override long Length => _baseStream.Length;

        public override long Position
        {
            get => _baseStream.Position;
            set => _baseStream.Position = value;
        }

        public ProgressStream(Stream baseStream)
        {
            _baseStream = baseStream;
        }

        public override void Flush()
        {
            _baseStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = _baseStream.Read(buffer, offset, count);
            BytesRead += result;
            BytesReadEvent?.Invoke(this, new BytesReadEventArgs(BytesRead));
            return result;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
            BytesRead += result;
            BytesReadEvent?.Invoke(this, new BytesReadEventArgs(BytesRead));
            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _baseStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _baseStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _baseStream.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _baseStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _baseStream.FlushAsync(cancellationToken);
        }
    }

    class BytesReadEventArgs : EventArgs
    {
        public BytesReadEventArgs(long bytesRead)
        {
            BytesRead = bytesRead;
        }

        public long BytesRead { get; }
    }
}
