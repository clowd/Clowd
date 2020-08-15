using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Com
{
    public class AlignedByteBuffer
    {
        public int Length => _size;

        private int _head;
        private int _tail;
        private int _size;
        private int _sizeUntilCut;
        private byte[] _buffer;


        public AlignedByteBuffer(int bufferSize)
        {
            _buffer = new byte[bufferSize];
        }

        public void Clear()
        {
            _head = 0;
            _tail = 0;
            _size = 0;
            _sizeUntilCut = _buffer.Length;
        }

        public void Clear(int size)
        {
            lock (this)
            {
                if (size > _size)
                    size = _size;

                if (size == 0)
                    return;

                _head = (_head + size) % _buffer.Length;
                _size -= size;

                if (_size == 0)
                {
                    _head = 0;
                    _tail = 0;
                }

                _sizeUntilCut = _buffer.Length - _head;
                return;
            }
        }

        public void SetCapacity(int capacity)
        {
            byte[] newBuffer = new byte[capacity];

            if (_size > 0)
            {
                if (_head < _tail)
                {
                    Buffer.BlockCopy(_buffer, _head, newBuffer, 0, _size);
                }
                else
                {
                    Buffer.BlockCopy(_buffer, _head, newBuffer, 0, _buffer.Length - _head);
                    Buffer.BlockCopy(_buffer, 0, newBuffer, _buffer.Length - _head, _tail);
                }
            }

            _head = 0;
            _tail = _size;
            _buffer = newBuffer;
        }

        public void Enqueue(byte[] buffer, int offset, int size)
        {
            if (size == 0)
                return;

            lock (this)
            {
                if ((_size + size) > _buffer.Length)
                    SetCapacity((_size + size + 2047) & ~2047);

                if (_head < _tail)
                {
                    int rightLength = (_buffer.Length - _tail);

                    if (rightLength >= size)
                    {
                        Buffer.BlockCopy(buffer, offset, _buffer, _tail, size);
                    }
                    else
                    {
                        Buffer.BlockCopy(buffer, offset, _buffer, _tail, rightLength);
                        Buffer.BlockCopy(buffer, offset + rightLength, _buffer, 0, size - rightLength);
                    }
                }
                else
                {
                    Buffer.BlockCopy(buffer, offset, _buffer, _tail, size);
                }

                _tail = (_tail + size) % _buffer.Length;
                _size += size;
                _sizeUntilCut = _buffer.Length - _head;
            }
        }

        public int Dequeue(byte[] buffer, int offset, int size)
        {
            lock (this)
            {
                if (size > _size)
                    size = _size;

                if (size == 0)
                    return 0;

                if (_head < _tail)
                {
                    Buffer.BlockCopy(_buffer, _head, buffer, offset, size);
                }
                else
                {
                    int rightLength = (_buffer.Length - _head);

                    if (rightLength >= size)
                    {
                        Buffer.BlockCopy(_buffer, _head, buffer, offset, size);
                    }
                    else
                    {
                        Buffer.BlockCopy(_buffer, _head, buffer, offset, rightLength);
                        Buffer.BlockCopy(_buffer, 0, buffer, offset + rightLength, size - rightLength);
                    }
                }

                _head = (_head + size) % _buffer.Length;
                _size -= size;

                if (_size == 0)
                {
                    _head = 0;
                    _tail = 0;
                }

                _sizeUntilCut = _buffer.Length - _head;
                return size;
            }
        }


        /// <summary>
        /// Peeks a byte with a relative index to the fHead
        /// Note: should be used for special cases only, as it is rather slow
        /// </summary>
        /// <param name="index">A relative index</param>
        /// <returns>The byte peeked</returns>
        public byte PeekOne(int index)
        {
            return index >= _sizeUntilCut
                ? _buffer[index - _sizeUntilCut]
                : _buffer[_head + index];
        }
    }
}
