using System;
using System.Threading;

namespace NAppUpdate.Framework.Common
{
	public class UpdateProcessAsyncResult : IAsyncResult
	{
		private readonly AsyncCallback _asyncCallback;
		private readonly Object _asyncState;

		private const Int32 StatePending = 0;
		private const Int32 StateCompletedSynchronously = 1;
		private const Int32 StateCompletedAsynchronously = 2;
		private Int32 _completedState = StatePending;

		private ManualResetEvent _asyncWaitHandle;
		private Exception _exception;

		public UpdateProcessAsyncResult(AsyncCallback asyncCallback, Object state)
		{
			_asyncCallback = asyncCallback;
			_asyncState = state;
		}

		public void SetAsCompleted(Exception exception, Boolean completedSynchronously)
		{
			// Passing null for exception means no error occurred. 
			// This is the common case
			_exception = exception;

			// The m_CompletedState field MUST be set prior calling the callback
			Int32 prevState = Interlocked.Exchange(ref _completedState,
			   completedSynchronously ? StateCompletedSynchronously : StateCompletedAsynchronously);
			if (prevState != StatePending)
				throw new InvalidOperationException("You can set a result only once");

			// If the event exists, set it
			if (_asyncWaitHandle != null) _asyncWaitHandle.Set();

			// If a callback method was set, call it
			if (_asyncCallback != null) _asyncCallback(this);
		}

		public void EndInvoke()
		{
			// This method assumes that only 1 thread calls EndInvoke 
			// for this object
			if (!IsCompleted)
			{
				// If the operation isn't done, wait for it
				AsyncWaitHandle.WaitOne();
				AsyncWaitHandle.Close();
				_asyncWaitHandle = null;  // Allow early GC
			}

			// Operation is done: if an exception occured, throw it
			if (_exception != null) throw _exception;
		}

		public bool IsCompleted
		{
			get
			{
				return Thread.VolatileRead(ref _completedState) != StatePending;
			}
		}

		public WaitHandle AsyncWaitHandle
		{
			get
			{
				if (_asyncWaitHandle == null)
				{
					bool done = IsCompleted;
					var mre = new ManualResetEvent(done);
					if (Interlocked.CompareExchange(ref _asyncWaitHandle, mre, null) != null)
					{
						// Another thread created this object's event; dispose 
						// the event we just created
						mre.Close();
					}
					else
					{
						if (!done && IsCompleted)
						{
							// If the operation wasn't done when we created 
							// the event but now it is done, set the event
							_asyncWaitHandle.Set();
						}
					}
				}
				return _asyncWaitHandle;
			}
		}

		public Object AsyncState { get { return _asyncState; } }

		public Boolean CompletedSynchronously
		{
			get
			{
				return Thread.VolatileRead(ref _completedState) == StateCompletedSynchronously;
			}
		}
	}
}
