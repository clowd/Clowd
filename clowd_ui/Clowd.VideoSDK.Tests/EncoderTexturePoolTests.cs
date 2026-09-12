using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Media;
using FFmpeg.AutoGen.Abstractions;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The bookkeeping under the zero-copy encode path's texture pool (EncoderTexturePool):
    // textures are opaque handles from a fake allocator here, so the rent / grow / return /
    // encoder-release protocol is checked without a GPU. The FFmpeg-facing half (a buffer
    // reference whose free callback returns the texture) needs the natives and skips without.
    public class EncoderTexturePoolTests
    {
        /// <summary>A fake allocator handing out distinct non-zero handles and recording releases.</summary>
        private sealed class FakeTextures
        {
            private long _next = 0x1000;
            public readonly List<IntPtr> Allocated = new List<IntPtr>();
            public readonly List<IntPtr> Released = new List<IntPtr>();

            public IntPtr Allocate()
            {
                var handle = (IntPtr)(_next += 0x10);
                Allocated.Add(handle);
                return handle;
            }

            public void Release(IntPtr handle) => Released.Add(handle);
        }

        [Fact]
        public void Rent_grows_on_demand_and_reuses_returned_textures()
        {
            var fake = new FakeTextures();
            using var pool = new EncoderTexturePool(fake.Allocate, fake.Release);
            Assert.Equal(0, pool.Count);

            var a = pool.Rent();
            var b = pool.Rent();
            var c = pool.Rent();
            Assert.Equal(3, pool.Count);
            Assert.Equal(3, pool.InUse);
            Assert.Equal(3, pool.PeakInUse);
            Assert.Equal(new[] { a, b, c }, fake.Allocated);

            // returned textures are handed out again before anything new is allocated
            pool.Return(b);
            pool.Return(a);
            Assert.Equal(1, pool.InUse);
            Assert.Equal(a, pool.Rent()); // most recently returned first
            Assert.Equal(b, pool.Rent());
            Assert.Equal(3, pool.Count);
            Assert.Equal(3, pool.PeakInUse);

            var d = pool.Rent(); // all three out again: grow
            Assert.Equal(4, pool.Count);
            Assert.Equal(4, pool.PeakInUse);
            Assert.NotEqual(IntPtr.Zero, d);
        }

        [Fact]
        public void Return_rejects_foreign_and_already_free_textures()
        {
            var fake = new FakeTextures();
            using var pool = new EncoderTexturePool(fake.Allocate, fake.Release);
            var a = pool.Rent();
            pool.Return(a);
            Assert.Throws<InvalidOperationException>(() => pool.Return(a));            // twice
            Assert.Throws<ArgumentException>(() => pool.Return((IntPtr)0x7777));       // not ours
        }

        [Fact]
        public void Dispose_releases_every_texture_once_whether_or_not_it_is_out()
        {
            var fake = new FakeTextures();
            var pool = new EncoderTexturePool(fake.Allocate, fake.Release);
            var a = pool.Rent();
            var b = pool.Rent();
            pool.Return(a);
            Assert.Equal(1, pool.InUse); // b is still out: an encoder that was not closed

            pool.Dispose();
            pool.Dispose(); // idempotent
            Assert.Equal(2, fake.Released.Count);
            Assert.Contains(a, fake.Released);
            Assert.Contains(b, fake.Released);
            Assert.Throws<ObjectDisposedException>(() => pool.Rent());
        }

        [Fact]
        public void An_allocator_failure_does_not_corrupt_the_pool()
        {
            int calls = 0;
            var fake = new FakeTextures();
            using var pool = new EncoderTexturePool(
                () => ++calls == 2 ? throw new InvalidOperationException("out of video memory") : fake.Allocate(),
                fake.Release);
            var a = pool.Rent();
            Assert.Throws<InvalidOperationException>(() => pool.Rent());
            Assert.Equal(1, pool.Count);
            Assert.Equal(1, pool.InUse);
            pool.Return(a);
            Assert.Equal(a, pool.Rent()); // still usable
        }

        /// <summary>The encoder-done signal: an AVBufferRef over a rented texture returns the
        /// texture when its last reference goes — the pool's free callback does not destroy
        /// anything. A second reference (what an encoder takes) keeps it out until that goes too.</summary>
        [Fact]
        public unsafe void Dropping_the_last_buffer_reference_returns_the_texture()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            var fake = new FakeTextures();
            using var pool = new EncoderTexturePool(fake.Allocate, fake.Release);
            var a = pool.Rent();

            var reference = pool.CreateReference(a);
            Assert.True(reference != null);
            // FFmpeg's D3D11 convention: the buffer's data is the frame descriptor naming the texture
            var descriptor = (AVD3D11FrameDescriptor*)reference->data;
            Assert.Equal(a, (IntPtr)descriptor->texture);
            Assert.Equal(0, descriptor->index);
            Assert.Equal(1, pool.InUse);

            var encoderHold = ffmpeg.av_buffer_ref(reference); // the encoder's reference
            ffmpeg.av_buffer_unref(&reference);                // ours goes first, as in the pipeline
            Assert.Equal(1, pool.InUse);                       // still with the encoder
            ffmpeg.av_buffer_unref(&encoderHold);
            Assert.Equal(0, pool.InUse);                       // back in the pool
            Assert.Empty(fake.Released);                       // and not destroyed
            Assert.Equal(a, pool.Rent());
        }

        /// <summary>A reference that outlives its pool (a frame nobody disposed) must not touch
        /// freed memory: the callback finds no pool and does nothing.</summary>
        [Fact]
        public unsafe void A_reference_released_after_the_pool_is_ignored()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            var fake = new FakeTextures();
            var pool = new EncoderTexturePool(fake.Allocate, fake.Release);
            var a = pool.Rent();
            var reference = pool.CreateReference(a);
            pool.Dispose();
            Assert.Single(fake.Released);
            ffmpeg.av_buffer_unref(&reference); // no crash, nothing to return to
            Assert.True(reference == null);
        }

        [Fact]
        public unsafe void CreateReference_requires_a_rented_texture()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            var fake = new FakeTextures();
            using var pool = new EncoderTexturePool(fake.Allocate, fake.Release);
            var a = pool.Rent();
            pool.Return(a);
            Assert.Throws<InvalidOperationException>(() => { pool.CreateReference(a); });
            Assert.Throws<ArgumentException>(() => { pool.CreateReference((IntPtr)0x4242); });
        }
    }
}
