using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Threading;
using System.Collections.Generic;

namespace Clowd.Util
{
    /// <summary>
    /// Provides a delegate factory for synchronizing events to a UI thread.
    /// </summary>
    public static class SynchronizationContextEventHandler
    {
        /// <summary>
        /// Creates a delegate which will post to the current synchronization context before calling the provided event handler.
        /// </summary>
        public static EventHandler<T> CreateDelegate<T>(EventHandler<T> action)
        {
            SynchronizationContext current = SynchronizationContext.Current;

            if (current == null)
                throw new InvalidOperationException("SynchronizationContext.Current is null. This delegate must be created on a thread which can be synchronized.");

            return (s, aq) =>
            {
                if (SynchronizationContext.Current == current)
                {
                    // already in the correct context
                    action(s, aq);
                }
                else
                {
                    // post event to target context
                    current.Post(delegate { action(s, aq); }, null);
                }
            };
        }
    }
}
