using System;
using System.Threading;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The single-instance forwarder's disposal, and the claim it rests on: the mutex is never owned,
    /// so Dispose releases nothing.
    /// </summary>
    /// <remarks>
    /// Neither test calls Startup, on purpose: Startup opens the fixed machine-wide mutex and, when it
    /// is already held, forwards over the fixed named pipe with a five second timeout, which on a
    /// developer machine with Prisc running would hang the suite and raise the user's app window. On a
    /// clean machine it would instead start a pipe server on that same global name, which Dispose
    /// cancels without joining, so two such tests in one run would race each other. The names are
    /// compile-time constants with no seam, and adding one would change the single-instance protocol
    /// only for a test.
    /// </remarks>
    public class MutexArgsForwarderTests
    {
        [Fact]
        public void Dispose_before_startup_does_not_throw()
        {
            // The constructor only builds a timer and a cancellation source, and Dispose only cancels
            // and disposes those, so this touches no global state.
            var forwarder = new Prisc.Util.MutexArgsForwarder();
            forwarder.Dispose();
        }

        [Fact]
        public void A_mutex_constructed_unowned_cannot_be_released()
        {
            // Why Dispose no longer calls ReleaseMutex: the forwarder's mutex is constructed with
            // initiallyOwned: false only to detect a second instance, so it was never owned and
            // releasing it always threw. Proved on a per-run name, never the machine-wide one.
            using var mutex = new Mutex(false, "PriscTest-" + Guid.NewGuid().ToString("N"), out _);
            Assert.Throws<ApplicationException>(() => mutex.ReleaseMutex());
        }
    }
}
