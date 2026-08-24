using System;
using System.IO;
using Clowd.UI.Helpers;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // What the editors' pickers and file drops accept, and the one file type they disagree about.
    // A .gif answers to both IsImage and IsMedia on purpose, so the drop and picker flows lean on
    // IsSingleFrame to decide what it becomes — these tests pin both halves of that, the second
    // against real GIF bytes rather than a hand-built MediaProbeResult, because the whole point of
    // the rule is what FFmpeg reports about a file on disk.
    public class MediaFileTypesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "clowd-gif-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best effort */ }
        }

        [Fact]
        public void Gif_IsBothAnImageAndMedia()
        {
            Assert.True(MediaFileTypes.IsImage("c:\\clips\\loop.gif"));
            Assert.True(MediaFileTypes.IsMedia("c:\\clips\\loop.gif"));
            Assert.True(MediaFileTypes.IsGif("c:\\clips\\LOOP.GIF"));
        }

        [Fact]
        public void OtherTypesStayOnOneSideOfTheLine()
        {
            Assert.True(MediaFileTypes.IsImage("shot.png"));
            Assert.False(MediaFileTypes.IsMedia("shot.png"));
            Assert.True(MediaFileTypes.IsMedia("clip.mp4"));
            Assert.False(MediaFileTypes.IsImage("clip.mp4"));
            Assert.True(MediaFileTypes.IsMedia("voice.mp3"));
            Assert.False(MediaFileTypes.IsGif("clip.mp4"));
            Assert.False(MediaFileTypes.IsGif(null));
            Assert.False(MediaFileTypes.IsImage("notes.txt"));
            Assert.False(MediaFileTypes.IsMedia("notes.txt"));
        }

        [Fact]
        public void SingleFrameGifIsAStill_AnimatedOneIsNot()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            Assert.True(MediaFileTypes.IsSingleFrame(Probe(Gif("still.gif", frames: 1))));
            Assert.False(MediaFileTypes.IsSingleFrame(Probe(Gif("anim.gif", frames: 2))));
        }

        private static MediaProbeResult Probe(string path) => MediaProbe.ProbeDetailed(path);

        private string Gif(string name, int frames) => TestGif.Write(Path.Combine(_dir, name), frames);
    }
}
