using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Server
{
    public class MultiStream : Stream
    {
        public bool DisposeChildren { get; set; }

        public long MinStreamLength { get; set; }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return _internalStream.CanWrite; }
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override long Length
        {
            get { throw new NotImplementedException(); }
        }

        public override long Position
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        private readonly MemoryStream _internalStream;
        private readonly object _lock = new object();
        private readonly List<ProducerStream> _trackedStreams = new List<ProducerStream>();
        public MultiStream(bool disposeChildrenOnClose = true)
        {
            DisposeChildren = disposeChildrenOnClose;
            _internalStream = new MemoryStream();
        }

        internal MemoryStream GetInternalMemoryStream()
        {
            return _internalStream;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                _internalStream.Write(buffer, offset, count);
            }
            // iterate stream in reverse as to allow removing of closed or unavailable streams
            for (int i = _trackedStreams.Count - 1; i >= 0; i--)
            {
                var stream = _trackedStreams[i];
                if (!stream.CanWrite)
                {
                    _trackedStreams.RemoveAt(i);
                    continue;
                }
                stream.Write(buffer, offset, count);
            }
        }
        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                base.Dispose(disposing);
                if (disposing)
                {
                    _internalStream.Dispose();
                    if (DisposeChildren)
                        _trackedStreams.ForEach(str => str.Dispose());
                }
            }
        }

        public Stream CloneStream()
        {
            lock (_lock)
            {
                const int readSize = 256;
                byte[] buffer = new byte[readSize];
                ProducerStream prod = new ProducerStream(MinStreamLength);
                long pos = _internalStream.Position;

                _internalStream.Position = 0;
                int count = _internalStream.Read(buffer, 0, readSize);
                while (count > 0)
                {
                    prod.Write(buffer, 0, count);
                    count = _internalStream.Read(buffer, 0, readSize);
                }
                _internalStream.Position = pos;

                _trackedStreams.Add(prod);
                //this cast is important, because the consumerstream write method will throw an exception
                //which is what is intended. the consuming thread should only be able to read.
                return (ConsumerStream)prod;
            }
        }

        private class ProducerStream : ConsumerStream
        {
            private long _writePosition = 0;
            public new bool CanWrite { get { return !Closed; } }
            public new void Write(byte[] buffer, int offset, int count)
            {
                lock (InnerStream)
                {
                    InnerStream.Position = _writePosition;
                    InnerStream.Write(buffer, offset, count);
                    _writePosition = InnerStream.Position;
                }
            }

            public ProducerStream(long minLength)
            {
                base._minLength = minLength;
            }
        }
        public class ConsumerStream : Stream
        {
            protected readonly MemoryStream InnerStream;
            protected bool Closed = false;
            private long _readPosition = 0;
            protected long _minLength = -1;

            protected ConsumerStream()
            {
                InnerStream = new MemoryStream();
            }

            public override bool CanRead { get { return !Closed; } }

            public override bool CanSeek { get { return !Closed; } }

            public override bool CanWrite { get { return false; } }

            public override void Flush()
            {
                throw new NotSupportedException();
            }

            public override long Length
            {
                get
                {
                    lock (InnerStream)
                    {
                        return Math.Max(InnerStream.Length, _minLength);
                    }
                }
            }

            public override long Position
            {
                //return and set read position, as that's what is expected from a read-only stream 
                get { return _readPosition; }
                set { _readPosition = value; }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int red = 0;
                while (_readPosition < _minLength && red == 0 && CanRead)
                {
                    if (_readPosition < InnerStream.Length)
                    {
                        lock (InnerStream)
                        {
                            InnerStream.Position = _readPosition;
                            red = InnerStream.Read(buffer, offset, count);
                            _readPosition = InnerStream.Position;
                        }
                    }
                    else
                    {
                        Thread.Sleep(500);
                    }
                }
                return red;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                lock (InnerStream)
                {
                    InnerStream.Position = _readPosition;
                    var rtrn = InnerStream.Seek(offset, origin);
                    _readPosition = InnerStream.Position;
                    return rtrn;
                }
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Closed = true;
                    InnerStream.Dispose();
                }
            }
        }
    }

}
