using System;
using System.IO;
using System.Text;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Writes the smallest yuv4mpeg file that declares a pixel aspect ratio, for the suites that
    /// exercise non-square-pixel import: a 2x2 4:2:0 grey canvas, N frames of six bytes each,
    /// and a header whose <c>A</c> tag is the sample aspect ratio FFmpeg's demuxer hands straight
    /// to the stream. Hand-written for the same reason <see cref="TestGif"/> is: the suite has no
    /// encoder, and the y4m header is a line of text.
    /// </summary>
    internal static class TestY4m
    {
        public static string Write(string path, int frames, int aspectNum, int aspectDen)
            => Write(path, frames, aspectNum, aspectDen, width: 2, height: 2);

        /// <summary>The same fixture at an arbitrary (even, for 4:2:0) size. Uncompressed, so a
        /// shape no encoder would accept - a 8194x2 frame - costs only its bytes.</summary>
        public static string Write(string path, int frames, int aspectNum, int aspectDen, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using var file = File.Create(path);
            var header = Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F30:1 Ip A{aspectNum}:{aspectDen} C420jpeg\n");
            file.Write(header, 0, header.Length);

            var frame = Encoding.ASCII.GetBytes("FRAME\n");
            var pixels = new byte[width * height + 2 * (width / 2) * (height / 2)]; // Y + Cb + Cr
            Array.Fill(pixels, (byte)0x80);
            for (var i = 0; i < frames; i++)
            {
                file.Write(frame, 0, frame.Length);
                file.Write(pixels, 0, pixels.Length);
            }

            return path;
        }
    }
}
