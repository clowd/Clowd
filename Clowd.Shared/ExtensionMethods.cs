using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RT.Util.ExtensionMethods;
using System.Net.Sockets;

namespace Clowd.Shared
{
    public static class Extensions
    {
        private const string _base36Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        public static string ToArbitraryBase(this long number, int toRadix)
        {
            if (toRadix < 2 || toRadix > 36)
                throw new ArgumentException("The radix must be >= 2 and <= 36");
            StringBuilder result = new StringBuilder();
            number = Math.Abs(number);
            while (number > 0)
            {
                result.Insert(0, _base36Characters[(int)(number % toRadix)]);
                number /= toRadix;
            }
            return result.ToString().ToLower();
        }
        public static long FromArbitraryBase(this string number, int fromRadix)
        {
            if (fromRadix < 2 || fromRadix > 36)
                throw new ArgumentException("The radix must be >= 2 and <= 36");
            number = number.ToUpper();
            long result = 0, multiplier = 1;
            foreach (var ch in number.ToCharArray().Reverse())
            {
                int position = _base36Characters.IndexOf(ch);
                if (position == -1 || position > fromRadix)
                    throw new ArgumentException("Invalid character in number input string");
                result += position * multiplier;
                multiplier *= fromRadix;
            }
            return result;
        }
        public static System.Drawing.Bitmap ResizeImage(this System.Drawing.Image image, int width, int height)
        {
            //a holder for the result
            System.Drawing.Bitmap result = new System.Drawing.Bitmap(width, height);
            //set the resolutions the same to avoid cropping due to resolution differences
            result.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            //use a graphics object to draw the resized image into the bitmap
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(result))
            {
                //set the resize quality modes to high quality
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                //draw the image into the target bitmap
                graphics.DrawImage(image, 0, 0, result.Width, result.Height);
            }

            //return the resulting bitmap
            return result;
        }
        public static IEnumerable<int> IndexOfAll(this string source, string search)
        {
            if (String.IsNullOrEmpty(search))
                throw new ArgumentException("search must not be empty", "search");
            for (int i = 0; ; i += search.Length)
            {
                i = source.IndexOf(search, i);
                if (i < 0)
                    break;
                yield return i;
            }
        }

        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException(cancellationToken);
            return await task;
        }
        public static Task ToTask(Action func)
        {
            return Task.Run(func);
        }
        public static Task<T> ToTask<T>(Func<T> func)
        {
            return Task.Run(func);
        }
        public static Task<T> ToTask<T>(Func<T> func, CancellationToken token)
        {
            return Task.Run(func).WithCancellation(token);
        }
        public static byte[] ReadUntil(this Stream stream, byte delimiter, bool includeDelimiter = true)
        {
            byte[] buffer = new byte[1024];
            int index = 0;

            while (true)
            {
                int cast;
                try
                {
                    cast = stream.ReadByte();
                }
                catch (Exception ex) when (ex is SocketException || ex is IOException)
                {
                    if (index == 0) return null;
                    Array.Resize(ref buffer, index);
                    return buffer;
                }
                if (cast == -1)
                {
                    //end of stream
                    Array.Resize(ref buffer, index);
                    return buffer;
                }
                byte b = (byte)cast;
                if (includeDelimiter || b != delimiter)
                {
                    if (index + 1 > buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                    buffer[index] = b;
                    index++;
                }
                if (b == delimiter)
                {
                    Array.Resize(ref buffer, index);
                    return buffer;
                }
            }
        }
        public static async Task<byte[]> ReadAsync(this Stream stream, int length)
        {
            byte[] buf = new byte[length];
            int read = await stream.FillBufferAsync(buf, 0, length);
            if (read < length)
                Array.Resize(ref buf, read);
            return buf;
        }
        public static async Task<byte[]> ReadAsync(this Stream stream, int length, CancellationToken token)
        {
            byte[] buf = new byte[length];
            int read = await stream.FillBufferAsync(buf, 0, length, token);
            if (read < length)
                Array.Resize(ref buf, read);
            return buf;
        }
        public static async Task<int> FillBufferAsync(this Stream stream, byte[] buffer, int offset, int length)
        {
            int totalRead = 0;
            while (length > 0)
            {
                var read = await stream.ReadAsync(buffer, offset, length);
                if (read == 0)
                    return totalRead;
                offset += read;
                length -= read;
                totalRead += read;
            }
            return totalRead;
        }
        public static async Task<int> FillBufferAsync(this Stream stream, byte[] buffer, int offset, int length, CancellationToken token)
        {
            int totalRead = 0;
            while (length > 0)
            {
                var read = await stream.ReadAsync(buffer, offset, length, token);
                if (read == 0)
                    return totalRead;
                offset += read;
                length -= read;
                totalRead += read;
            }
            return totalRead;
        }

        public static string ToPrettySizeString(this long bytes, int decimalPlaces = 2)
        {
            if (bytes < 1000) return bytes + " B";
            if (bytes < 1000000) return Math.Round(bytes / (double)1000, decimalPlaces) + " KB";
            if (bytes < 1000000000) return Math.Round(bytes / (double)1000000, decimalPlaces) + " MB";
            return Math.Round(bytes / (double)1000000000, decimalPlaces) + " GB";
        }
    }
}
