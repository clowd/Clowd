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

    /// <summary>
    /// Create a new instance of this class within a using() block, and set a new Synchronization Context.
    /// Most useful in conjunction with async calls to prevent them from looking to re-enter the calling context
    /// which can cause deadlocks.
    /// </summary>
    public class SynchronizationContextChange : IDisposable
    {
        private SynchronizationContext _previous;

        /// <summary>
        /// Creates a new instance of SynchronizationContextChange
        /// </summary>
        /// <param name="newContext">The new SynchronizationContext, or null.</param>
        public SynchronizationContextChange(SynchronizationContext newContext = null)
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(newContext);
        }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }

    /// <summary>
    /// Flags that identify differences in behavior in various <see cref="SynchronizationContext"/> implementations.
    /// </summary>
    [Flags]
    public enum SynchronizationContextProperties
    {
        /// <summary>
        /// The <see cref="SynchronizationContext"/> makes no guarantees about any of the properties in <see cref="SynchronizationContextProperties"/>.
        /// </summary>
        None = 0x0,

        /// <summary>
        /// <see cref="SynchronizationContext.Post"/> is guaranteed to be non-reentrant (if called from a thread that is not the <see cref="SynchronizationContext"/>'s specific associated thread, if any).
        /// </summary>
        NonReentrantPost = 0x1,

        /// <summary>
        /// <see cref="SynchronizationContext.Send"/> is guaranteed to be non-reentrant (if called from a thread that is not the <see cref="SynchronizationContext"/>'s specific associated thread, if any).
        /// </summary>
        NonReentrantSend = 0x2,

        /// <summary>
        /// Delegates queued to the <see cref="SynchronizationContext"/> are guaranteed to execute one at a time.
        /// </summary>
        Synchronized = 0x4,

        /// <summary>
        /// Delegates queued to the <see cref="SynchronizationContext"/> are guaranteed to execute in order. Any <see cref="SynchronizationContext"/> claiming to be <see cref="Sequential"/> should also claim to be <see cref="Synchronized"/>.
        /// </summary>
        Sequential = 0x8,

        /// <summary>
        /// The <see cref="SynchronizationContext"/> has exactly one managed thread associated with it. Any <see cref="SynchronizationContext"/> specifying <see cref="SpecificAssociatedThread"/> should also specify <see cref="Synchronized"/>.
        /// </summary>
        SpecificAssociatedThread = 0x10,

        /// <summary>
        /// The <see cref="SynchronizationContext"/> makes the standard guarantees (<see cref="NonReentrantPost"/>, <see cref="NonReentrantSend"/>, <see cref="Synchronized"/>, <see cref="Sequential"/>, and <see cref="SpecificAssociatedThread"/>). This is defined as a constant because most custom synchronization contexts do make these guarantees.
        /// </summary>
        Standard = NonReentrantPost | NonReentrantSend | Synchronized | Sequential | SpecificAssociatedThread,
    }

    /// <summary>
    /// A global register of <see cref="SynchronizationContextProperties"/> flags for <see cref="SynchronizationContext"/> types.
    /// </summary>
    public static class SynchronizationContextRegister
    {
        /// <summary>
        /// A mapping from synchronization context type names to their properties. We map from type names instead of actual types to avoid dependencies on unnecessary assemblies.
        /// </summary>
        private static readonly Dictionary<string, SynchronizationContextProperties> synchronizationContextProperties = PredefinedSynchronizationContextProperties();

        /// <summary>
        /// Registers a <see cref="SynchronizationContext"/> type claiming to provide certain guarantees.
        /// </summary>
        /// <param name="synchronizationContextType">The type derived from <see cref="SynchronizationContext"/>. May not be <c>null</c>.</param>
        /// <param name="properties">The guarantees provided by this type.</param>
        /// <remarks>
        /// <para>This method should be called once for each type of <see cref="SynchronizationContext"/>. It is not necessary to call this method for .NET <see cref="SynchronizationContext"/> types or <see cref="ActionDispatcherSynchronizationContext"/>.</para>
        /// <para>If this method is called more than once for a type, the new value of <paramref name="properties"/> replaces the old value. The flags are not merged.</para>
        /// </remarks>
        public static void Register(Type synchronizationContextType, SynchronizationContextProperties properties)
        {
            Contract.Requires(synchronizationContextType != null);

            lock (synchronizationContextProperties)
            {
                if (synchronizationContextProperties.ContainsKey(synchronizationContextType.FullName))
                {
                    synchronizationContextProperties[synchronizationContextType.FullName] = properties;
                }
                else
                {
                    synchronizationContextProperties.Add(synchronizationContextType.FullName, properties);
                }
            }
        }

        /// <summary>
        /// Looks up the guarantees for a <see cref="SynchronizationContext"/> type.
        /// </summary>
        /// <param name="synchronizationContextType">The type derived from <see cref="SynchronizationContext"/> to test. May not be <c>null</c>.</param>
        /// <returns>The properties guaranteed by <paramref name="synchronizationContextType"/>.</returns>
        public static SynchronizationContextProperties Lookup(Type synchronizationContextType)
        {
            Contract.Requires(synchronizationContextType != null);

            lock (synchronizationContextProperties)
            {
                SynchronizationContextProperties supported = SynchronizationContextProperties.None;
                if (synchronizationContextProperties.ContainsKey(synchronizationContextType.FullName))
                {
                    supported = synchronizationContextProperties[synchronizationContextType.FullName];
                }

                return supported;
            }
        }

        /// <summary>
        /// Verifies that a <see cref="SynchronizationContext"/> satisfies the guarantees required by the calling code.
        /// </summary>
        /// <param name="synchronizationContextType">The type derived from <see cref="SynchronizationContext"/> to test. May not be <c>null</c>.</param>
        /// <param name="properties">The guarantees required by the calling code.</param>
        public static void Verify(Type synchronizationContextType, SynchronizationContextProperties properties)
        {
            Contract.Requires(synchronizationContextType != null);

            SynchronizationContextProperties supported = Lookup(synchronizationContextType);
            if ((supported & properties) != properties)
            {
                throw new InvalidOperationException("This asynchronous object cannot be used with this SynchronizationContext");
            }
        }

        /// <summary>
        /// Verifies that <see cref="SynchronizationContext.Current"/> satisfies the guarantees required by the calling code.
        /// </summary>
        /// <param name="properties">The guarantees required by the calling code.</param>
        public static void Verify(SynchronizationContextProperties properties)
        {
            if (SynchronizationContext.Current == null)
            {
                Verify(typeof(SynchronizationContext), properties);
            }
            else
            {
                Verify(SynchronizationContext.Current.GetType(), properties);
            }
        }

        /// <summary>
        /// Returns the mapping for all predefined (.NET) <see cref="SynchronizationContext"/> types.
        /// </summary>
        /// <returns>The mapping for all predefined (.NET) <see cref="SynchronizationContext"/> types.</returns>
        private static Dictionary<string, SynchronizationContextProperties> PredefinedSynchronizationContextProperties()
        {
            var ret = new Dictionary<string, SynchronizationContextProperties>
            {
                { "System.Threading.SynchronizationContext", SynchronizationContextProperties.NonReentrantPost },
                { "System.Windows.Forms.WindowsFormsSynchronizationContext", SynchronizationContextProperties.Standard },
                { "System.Windows.Threading.DispatcherSynchronizationContext", SynchronizationContextProperties.Standard }
            };

            // AspNetSynchronizationContext does not provide any guarantees at all, so it is not added here
            return ret;
        }
    }

    /// <summary>
    /// Allows objects that use <see cref="ISynchronizeInvoke"/> (usually using a property named SynchronizingObject) to synchronize to a generic <see cref="SynchronizationContext"/>.
    /// </summary>
    /// <remarks>
    /// <para>.NET framework types that use <see cref="ISynchronizeInvoke"/> include <see cref="System.Timers.Timer">System.Timers.Timer</see>, <see cref="System.Diagnostics.EventLog">System.Diagnostics.EventLog</see>, <see cref="System.Diagnostics.Process">System.Diagnostics.Process</see>, and <see cref="System.IO.FileSystemWatcher">System.IO.FileSystemWatcher</see>.</para>
    /// <para>This class does not invoke <see cref="SynchronizationContext.OperationStarted"/> or <see cref="SynchronizationContext.OperationCompleted"/>, so for some synchronization contexts, these may need to be called explicitly in addition to using this class. ASP.NET do require them to be called; Windows Forms, WPF, free threads, and <see cref="ActionDispatcher"/> do not.</para>
    /// </remarks>
    /// <threadsafety static="true" instance="true"/>
    /// <example>
    /// The following code example demonstrates how GenericSynchronizingObject may be used to redirect FileSystemWatcher events to an ActionThread:
    /// <code source="..\..\Source\Examples\DocumentationExamples\GenericSynchronizingObject\WithFileSystemWatcher.cs"/>
    /// The code example above produces this output:
    /// <code lang="None" title="Output">
    /// ActionThread thread ID is 3
    /// FileSystemWriter.Created thread ID is 3
    /// </code>
    /// </example>
    public sealed class SynchronizationContextProxy : ISynchronizeInvoke
    {
        /// <summary>
        /// The captured synchronization context.
        /// </summary>
        private readonly SynchronizationContext synchronizationContext;

        /// <summary>
        /// The managed thread id of the synchronization context's specific associated thread, if any.
        /// </summary>
        private readonly int? synchronizationContextThreadId;

        /// <summary>
        /// Initializes a new instance of the <see cref="SynchronizationContextProxy"/> class, binding to <see cref="SynchronizationContext.Current">SynchronizationContext.Current</see>.
        /// </summary>
        /// <example>
        /// The following code example demonstrates how GenericSynchronizingObject may be used to redirect FileSystemWatcher events to an ActionThread:
        /// <code source="..\..\Source\Examples\DocumentationExamples\GenericSynchronizingObject\WithFileSystemWatcher.cs"/>
        /// The code example above produces this output:
        /// <code lang="None" title="Output">
        /// ActionThread thread ID is 3
        /// FileSystemWriter.Created thread ID is 3
        /// </code>
        /// </example>
        public SynchronizationContextProxy()
        {
            // (This method is always invoked from a SynchronizationContext thread)
            this.synchronizationContext = SynchronizationContext.Current ?? new SynchronizationContext();

            if ((SynchronizationContextRegister.Lookup(this.synchronizationContext.GetType()) & SynchronizationContextProperties.SpecificAssociatedThread) == SynchronizationContextProperties.SpecificAssociatedThread)
            {
                this.synchronizationContextThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the current thread must invoke a delegate.
        /// </summary>
        /// <remarks>
        /// <para>If there is not enough information about the synchronization context to determine this value, then this property evaluates to <c>false</c>. This is done because a cross-thread exception is easier to diagnose than a deadlock.</para>
        /// </remarks>
        public bool InvokeRequired
        {
            get
            {
                if (this.synchronizationContextThreadId != null)
                {
                    return this.synchronizationContextThreadId != Thread.CurrentThread.ManagedThreadId;
                }

                string name = this.synchronizationContext.GetType().Name;
                if (name == "SynchronizationContext")
                {
                    return !Thread.CurrentThread.IsThreadPoolThread;
                }

                // Unfortunately, there is no way to determine InvokeRequired for arbitrary contexts without specific associated threads.
                // So, we just return false. This will result in correct behavior on all existing SynchronizationContext implementations,
                //  but may cause a cross-threading exception if some weird new SynchronizationContext is invented in the future.
                return false;
            }
        }

        [ContractInvariantMethod]
        private void ObjectInvariant()
        {
            Contract.Invariant(this.synchronizationContext != null);
        }

        /// <summary>
        /// Starts the invocation of a delegate synchronized by the <see cref="SynchronizationContext"/> of the thread that created this <see cref="SynchronizationContextProxy"/>. A corresponding call to <see cref="EndInvoke"/> is not required.
        /// </summary>
        /// <param name="method">The delegate to run. May not be <c>null</c>.</param>
        /// <param name="args">The arguments to pass to <paramref name="method"/>. May be <c>null</c> if the delegate does not require arguments.</param>
        /// <returns>An <see cref="IAsyncResult"/> that can be used to detect completion of the delegate.</returns>
        /// <remarks>
        /// <para>If the <see cref="SynchronizationContext.Post"/> for this object's synchronization context is reentrant, then this method is also reentrant.</para>
        /// </remarks>
        public IAsyncResult BeginInvoke(Delegate method, object[] args)
        {
            Contract.Ensures(Contract.Result<IAsyncResult>() != null);
            Contract.Assume(method != null);

            // (This method may be invoked from any thread)
            IAsyncResult ret = new AsyncResult();

            // (The delegate passed to Post will run in the thread chosen by the SynchronizationContext)
            this.synchronizationContext.Post(
                (SendOrPostCallback)delegate (object state)
                {
                    AsyncResult result = (AsyncResult)state;
                    try
                    {
                        result.ReturnValue = method.DynamicInvoke(args);
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex;
                    }

                    result.Done();
                },
                ret);
            return ret;
        }

        /// <summary>
        /// Waits for the invocation of a delegate to complete, and returns the result of the delegate. This may only be called once for a given <see cref="IAsyncResult"/> object.
        /// </summary>
        /// <param name="result">The <see cref="IAsyncResult"/> returned from a call to <see cref="BeginInvoke"/>.</param>
        /// <returns>The result of the delegate. May not be <c>null</c>.</returns>
        /// <remarks>
        /// <para>If the delegate raised an exception, then this method will raise a <see cref="System.Reflection.TargetInvocationException"/> with that exception as the <see cref="Exception.InnerException"/> property.</para>
        /// </remarks>
        public object EndInvoke(IAsyncResult result)
        {
            Contract.Assume(result != null);
            Contract.Assume(result.GetType() == typeof(AsyncResult));

            // (This method may be invoked from any thread)
            AsyncResult asyncResult = (AsyncResult)result;
            asyncResult.WaitForAndDispose();
            if (asyncResult.Error != null)
            {
                throw asyncResult.Error;
            }

            return asyncResult.ReturnValue;
        }

        /// <summary>
        /// Synchronously invokes a delegate synchronized with the <see cref="SynchronizationContext"/> of the thread that created this <see cref="SynchronizationContextProxy"/>.
        /// </summary>
        /// <param name="method">The delegate to invoke. May not be <c>null</c>.</param>
        /// <param name="args">The parameters for <paramref name="method"/>. May be <c>null</c> if the delegate does not require arguments.</param>
        /// <returns>The result of the delegate.</returns>
        /// <remarks>
        /// <para>If the <see cref="SynchronizationContext.Send"/> for this object's synchronization context is reentrant, then this method is also reentrant.</para>
        /// <para>If the delegate raises an exception, then this method will raise a <see cref="System.Reflection.TargetInvocationException"/> with that exception as the <see cref="Exception.InnerException"/> property.</para>
        /// </remarks>
        public object Invoke(Delegate method, object[] args)
        {
            Contract.Assume(method != null);

            // (This method may be invoked from any thread)
            var ret = new ReturnValue();
            this.synchronizationContext.Send(
                _ =>
                {
                    try
                    {
                        ret.ReturnedValue = method.DynamicInvoke(args);
                    }
                    catch (Exception ex)
                    {
                        ret.Error = ex;
                    }
                },
                null);
            if (ret.Error != null)
            {
                throw ret.Error;
            }

            return ret.ReturnedValue;
        }

        /// <summary>
        /// A helper object that just wraps the return value, when the delegate is invoked synchronously.
        /// </summary>
        private sealed class ReturnValue
        {
            /// <summary>
            /// Gets or sets return value, if any. This is only valid if <see cref="Error"/> is not <c>null</c>. May be <c>null</c>, even if valid.
            /// </summary>
            public object ReturnedValue { get; set; }

            /// <summary>
            /// Gets or sets the error, if any. May be <c>null</c>.
            /// </summary>
            public Exception Error { get; set; }
        }

        // Note that our implementation of AsyncResult differs significantly from that presented in "Implementing the CLR Asynchronous
        //  Programming Model", MSDN 2007-03, Jeffrey Richter. They take a lock-free approach, while we use explicit locks.
        // Some of the major differences:
        //  1) Ours is simplified, not handling synchronous completion, user-defined states, or callbacks.
        //  2) We use a lock instead of interlocked variables for these reasons:
        //    a) Locks tend to scale better as the number of CPUs increase (they only affect a single thread while interlocked affects
        //       the instruction cache of every CPU).
        //    b) Code is easier to read and understand that there are no race conditions.
        //    c) We do handle the situation where a WaitHandle is created earlier but not immediately used for synchronization. This is
        //       rare in practice.
        //    d) Race conditions are handled more efficiently. This is also rare in practice.
        //  3) However, we do require the allocation of a lock for every AsyncResult instance, so our solution does use more resources.

        /// <summary>
        /// A helper object that holds the return value and also allows waiting for the asynchronous completion of a delegate.
        /// Note that calling <see cref="ISynchronizeInvoke.EndInvoke"/> is optional, and this class is optimized for that common use case.
        /// </summary>
        private sealed class AsyncResult : IAsyncResult
        {
            /// <summary>
            /// The wait handle, which may be null. Writes are synchronized using Interlocked access.
            /// </summary>
            private ManualResetEvent asyncWaitHandle;

            /// <summary>
            /// Whether the operation has completed. Synchronized using atomic reads/writes and Interlocked access.
            /// </summary>
            private bool isCompleted;

            /// <summary>
            /// Object used for synchronization.
            /// </summary>
            private readonly object syncObject = new object();

            /// <summary>
            /// Gets or sets the return value. Must be set before calling <see cref="Done"/>.
            /// </summary>
            public object ReturnValue { get; set; }

            /// <summary>
            /// Gets or sets the error. Must be set before calling <see cref="Done"/>.
            /// </summary>
            public Exception Error { get; set; }

            /// <summary>
            /// Gets the user-defined state. Always returns <c>null</c>; user-defined state is not supported.
            /// </summary>
            /// <remarks>
            /// <para>This property may be accessed in an arbitrary thread context.</para>
            /// </remarks>
            public object AsyncState
            {
                get { return null; }
            }

            /// <summary>
            /// Gets a waitable handle for this operation.
            /// </summary>
            /// <remarks>
            /// <para>This property may be accessed in an arbitrary thread context.</para>
            /// </remarks>
            public WaitHandle AsyncWaitHandle
            {
                get
                {
                    Contract.Ensures(Contract.Result<WaitHandle>() != null);
                    Contract.Ensures(this.asyncWaitHandle != null);

                    lock (this.syncObject)
                    {
                        // Create a new one if it doesn't already exist
                        if (this.asyncWaitHandle == null)
                        {
                            this.asyncWaitHandle = new ManualResetEvent(this.isCompleted);
                        }
                    }

                    Contract.Assume(this.asyncWaitHandle != null);
                    return this.asyncWaitHandle;
                }
            }

            /// <summary>
            /// Gets a value indicating whether the operation completed synchronously. Always returns false; synchronous completion is not supported.
            /// </summary>
            /// <remarks>
            /// <para>This property may be accessed in an arbitrary thread context.</para>
            /// </remarks>
            public bool CompletedSynchronously
            {
                get { return false; }
            }

            /// <summary>
            /// Gets a value indicating whether this operation has completed.
            /// </summary>
            /// <remarks>
            /// <para>This property may be accessed in an arbitrary thread context.</para>
            /// </remarks>
            public bool IsCompleted
            {
                get
                {
                    lock (this.syncObject)
                    {
                        return this.isCompleted;
                    }
                }
            }

            /// <summary>
            /// Marks the AsyncResult object as done. Should only be called once.
            /// </summary>
            /// <remarks>
            /// <para>This method always runs in the SynchronizationContext thread.</para>
            /// </remarks>
            public void Done()
            {
                lock (this.syncObject)
                {
                    this.isCompleted = true;

                    // Set the wait handle, only if necessary
                    if (this.asyncWaitHandle != null)
                    {
                        this.asyncWaitHandle.Set();
                    }
                }
            }

            /// <summary>
            /// Waits for the pending operation to complete, if necessary, and frees all resources. Should only be called once.
            /// </summary>
            /// <remarks>
            /// <para>This method may run in an arbitrary thread context.</para>
            /// </remarks>
            public void WaitForAndDispose()
            {
                // First, do a simple check to see if it's completed
                if (this.IsCompleted)
                {
                    // Ensure the underlying wait handle is disposed if necessary
                    lock (this.syncObject)
                    {
                        if (this.asyncWaitHandle != null)
                        {
                            this.asyncWaitHandle.Close();
                            this.asyncWaitHandle = null;
                        }
                    }

                    return;
                }

                // Wait for the signal that it's completed, creating the signal if necessary
                this.AsyncWaitHandle.WaitOne();

                // Now that it's completed, dispose of the underlying wait handle
                lock (this.syncObject)
                {
                    this.asyncWaitHandle.Close();
                    this.asyncWaitHandle = null;
                }
            }
        }
    }
}
