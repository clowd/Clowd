using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileUploadLib.Providers
{
    public class AzureBlobStream : Stream
    {
        internal CloudBlockBlob Blob { get; }
        internal string UploadKey { get; }
        internal string FileName { get; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => _isWritable;

        public override long Length => _bytesWritten;

        public bool Gzip { get; }

        const int blockSize = 256 * 1024;
        private long _bytesWritten = 0;
        private List<string> blockIds = new List<string>();
        private byte[] blockBuffer = new byte[blockSize];
        private int bufferIdx = 0;
        private string blockPrefix;
        private bool _isWritable = true;

        public override long Position { get => _bytesWritten; set => throw new NotImplementedException(); }

        internal AzureBlobStream(CloudBlockBlob blob, string uploadKey, string fileName, bool gzip)
        {
            Blob = blob;
            UploadKey = uploadKey;
            FileName = fileName;
            blob.StreamWriteSizeInBytes = blockSize;
            blockPrefix = Guid.NewGuid().ToString().ToLower().Replace("-", "");
            Gzip = gzip;
        }

        public override void Flush()
        {
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
            if (!CanWrite)
                throw new InvalidOperationException("This stream can no longer be written to");

            Console.WriteLine($"WRITE {count} bytes");
            if (count + bufferIdx < blockSize)
            {
                Array.Copy(buffer, offset, blockBuffer, bufferIdx, count);
                bufferIdx += count;
            }
            else
            {
                var allowedBytes = blockSize - bufferIdx;
                Array.Copy(buffer, offset, blockBuffer, bufferIdx, allowedBytes);
                bufferIdx += allowedBytes;
                FlushBlockBuffer();
                Write(buffer, offset + allowedBytes, count - allowedBytes);
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!CanWrite)
                throw new InvalidOperationException("This stream can no longer be written to");

            Console.WriteLine($"ASYNC WRITE {count} bytes");
            if (count + bufferIdx < blockSize)
            {
                Array.Copy(buffer, offset, blockBuffer, bufferIdx, count);
                bufferIdx += count;
            }
            else
            {
                var allowedBytes = blockSize - bufferIdx;
                Array.Copy(buffer, offset, blockBuffer, bufferIdx, allowedBytes);
                bufferIdx += allowedBytes;
                await FlushBlockBufferAsync(cancellationToken);
                await WriteAsync(buffer, offset + allowedBytes, count - allowedBytes);
            }
        }

        public override void WriteByte(byte value)
        {
            blockBuffer[bufferIdx] = value;
            bufferIdx++;

            if (bufferIdx == blockSize)
                FlushBlockBuffer();
        }

        private void FlushBlockBuffer()
        {
            var blockId = blockPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(blockIds.Count.ToString("d6")));
            blockIds.Add(blockId);
            Blob.PutBlock(blockId, new MemoryStream(blockBuffer), null);
            _bytesWritten += bufferIdx;
            Console.WriteLine($"PUT block.{blockIds.Count} - {bufferIdx} bytes. ({_bytesWritten} total)");
            bufferIdx = 0;
        }

        private async Task FlushBlockBufferAsync(CancellationToken cancellationToken)
        {
            var blockId = blockPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(blockIds.Count.ToString("d6")));
            blockIds.Add(blockId);
            await Blob.PutBlockAsync(blockId, new MemoryStream(blockBuffer), null, cancellationToken);
            _bytesWritten += bufferIdx;
            Console.WriteLine($"ASYNC PUT block.{blockIds.Count} - {bufferIdx} bytes. ({_bytesWritten} total)");
            bufferIdx = 0;
        }

        internal async Task CommitBlocks()
        {
            _isWritable = false;

            if (bufferIdx > 0)
            {
                var commit = new byte[bufferIdx];
                Array.Copy(blockBuffer, 0, commit, 0, bufferIdx);
                var blockId = blockPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(blockIds.Count.ToString("d6")));
                blockIds.Add(blockId);
                await Blob.PutBlockAsync(blockId, new MemoryStream(commit), null);
                _bytesWritten += bufferIdx;
                Console.WriteLine($"ASYNC PUT block.{blockIds.Count} - {bufferIdx} bytes. ({_bytesWritten} total)");
            }

            Console.WriteLine("ASYNC COMMIT START");
            await Blob.PutBlockListAsync(blockIds);
            Console.WriteLine("ASYNC COMMIT END");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
