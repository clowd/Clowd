using System.IO;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Writes the smallest GIF89a that can carry N frames, for the suites that exercise GIF import:
    /// a 1x1 canvas over a two-color global table (black, white), each frame a graphic-control
    /// block (10ms delay) and a one-pixel LZW image alternating between the two table entries — so
    /// frame 0 is black, frame 1 white, frame 2 black again, and a decoded pixel says which frame
    /// it came from.
    ///
    /// Hand-written rather than encoded because the suite has no GIF encoder, and because 43 bytes
    /// of literal container is easier to trust than a fixture built by the code under test.
    /// </summary>
    internal static class TestGif
    {
        public static string Write(string path, int frames)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using var file = File.Create(path);
            // "GIF89a", 1x1, global color table of two entries, no background, no aspect.
            Emit(file, 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00);
            Emit(file, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF);

            for (var i = 0; i < frames; i++)
            {
                // graphic control extension: 10ms delay, no disposal, no transparency.
                Emit(file, 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00);
                // image descriptor: at 0,0, 1x1, no local color table.
                Emit(file, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00);
                // LZW, min code size 2: clear (4), one pixel of color i % 2, end-of-information (5).
                Emit(file, 0x02, 0x02, (byte)(i % 2 == 0 ? 0x44 : 0x4C), 0x01, 0x00);
            }

            Emit(file, 0x3B); // trailer
            return path;
        }

        private static void Emit(Stream stream, params byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    }
}
