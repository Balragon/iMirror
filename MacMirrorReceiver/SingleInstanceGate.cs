using System;
using System.Threading;

namespace MacMirrorReceiver;

// The settings "Save & Restart" flow starts the replacement process before the old
// process has finished shutting down, so the replacement can lose the single-instance
// mutex race and silently exit, leaving the user with no running iMirror. Waiting a
// bounded time for the previous holder to exit keeps restart handoffs reliable while
// still rejecting true duplicate launches.
internal static class SingleInstanceGate
{
	public static bool TryAcquireExistingMutex(Mutex mutex, TimeSpan timeout)
	{
		if (mutex == null)
		{
			throw new ArgumentNullException(nameof(mutex));
		}

		try
		{
			return mutex.WaitOne(timeout);
		}
		catch (AbandonedMutexException)
		{
			// The previous instance exited without releasing the mutex; ownership has
			// been transferred to this waiter, so this process may continue as the
			// single running instance.
			return true;
		}
	}
}
