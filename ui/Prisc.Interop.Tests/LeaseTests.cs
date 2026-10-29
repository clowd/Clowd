using System;
using System.Threading;
using Xunit;

namespace Prisc.Interop.Tests
{
    public sealed class LeaseTests
    {
        /// <summary>A handle whose destroy is recorded instead of calling the library.</summary>
        private sealed class FakeHandle : InteropHandle
        {
            public int Destroyed;

            public FakeHandle() => Adopt((IntPtr)0x1234);

            protected override bool RequiresLibrary => false;

            protected override int Destroy(IntPtr h)
            {
                Assert.Equal((IntPtr)0x1234, h);
                Interlocked.Increment(ref Destroyed);
                return 0;
            }
        }

        [Fact]
        public void Dispose_during_a_leased_call_defers_destroy_until_the_lease_ends()
        {
            var handle = new FakeHandle();
            var lease = handle.Lease();
            Assert.Equal((IntPtr)0x1234, lease.Handle);

            handle.Dispose();
            Assert.Equal(0, handle.Destroyed);
            // disposed: no new call may start, while the one in flight keeps the handle alive
            Assert.Throws<ObjectDisposedException>(() =>
            {
                using var second = handle.Lease();
            });

            lease.Dispose();
            Assert.Equal(1, handle.Destroyed);
            lease.Dispose();
            Assert.Equal(1, handle.Destroyed);
        }

        [Fact]
        public void A_lease_after_dispose_is_a_managed_error()
        {
            var handle = new FakeHandle();
            handle.Dispose();
            Assert.Equal(1, handle.Destroyed);
            Assert.Throws<ObjectDisposedException>(() =>
            {
                using var lease = handle.Lease();
            });
        }

        [Fact]
        public void A_lease_held_on_another_thread_keeps_the_handle_alive()
        {
            var handle = new FakeHandle();
            using var inCall = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                var lease = handle.Lease();
                inCall.Set();
                release.Wait();
                lease.Dispose();
            });
            worker.Start();
            inCall.Wait();
            handle.Dispose();
            Assert.Equal(0, handle.Destroyed);
            release.Set();
            worker.Join();
            Assert.Equal(1, handle.Destroyed);
        }
    }
}
