using System;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class SingleInstanceGateTests
{
	private static string UniqueMutexName()
	{
		return @"Local\iMirror.Tests." + Guid.NewGuid().ToString("N");
	}

	[Fact]
	public void TryAcquireExistingMutex_ReturnsTrue_WhenMutexIsUnowned()
	{
		using var mutex = new Mutex(initiallyOwned: false, UniqueMutexName());

		bool acquired = SingleInstanceGate.TryAcquireExistingMutex(mutex, TimeSpan.FromMilliseconds(100));

		Assert.True(acquired);
		mutex.ReleaseMutex();
	}

	[Fact]
	public void TryAcquireExistingMutex_ReturnsFalse_WhenAnotherThreadKeepsHoldingIt()
	{
		string name = UniqueMutexName();
		using var holderAcquired = new ManualResetEventSlim(false);
		using var releaseHolder = new ManualResetEventSlim(false);

		var holder = new Thread(() =>
		{
			using var holderMutex = new Mutex(initiallyOwned: true, name);
			holderAcquired.Set();
			releaseHolder.Wait(TimeSpan.FromSeconds(10));
			holderMutex.ReleaseMutex();
		});
		holder.IsBackground = true;
		holder.Start();
		Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));

		using var waiterMutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
		Assert.False(createdNew);

		bool acquired = SingleInstanceGate.TryAcquireExistingMutex(waiterMutex, TimeSpan.FromMilliseconds(100));

		Assert.False(acquired);
		releaseHolder.Set();
		holder.Join(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public void TryAcquireExistingMutex_ReturnsTrue_WhenHolderReleasesWithinTimeout()
	{
		string name = UniqueMutexName();
		using var holderAcquired = new ManualResetEventSlim(false);

		var holder = new Thread(() =>
		{
			using var holderMutex = new Mutex(initiallyOwned: true, name);
			holderAcquired.Set();
			Thread.Sleep(150);
			holderMutex.ReleaseMutex();
		});
		holder.IsBackground = true;
		holder.Start();
		Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));

		using var waiterMutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
		Assert.False(createdNew);

		bool acquired = SingleInstanceGate.TryAcquireExistingMutex(waiterMutex, TimeSpan.FromSeconds(10));

		Assert.True(acquired);
		holder.Join(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public void TryAcquireExistingMutex_ReturnsTrue_WhenHolderAbandonsTheMutex()
	{
		string name = UniqueMutexName();
		using var holderAcquired = new ManualResetEventSlim(false);

		// The holder thread exits while still owning the mutex, mirroring a process
		// that terminates without releasing. The waiter then receives ownership via
		// AbandonedMutexException, which the gate must treat as a successful acquire.
		var holder = new Thread(() =>
		{
			var holderMutex = new Mutex(initiallyOwned: true, name);
			GC.KeepAlive(holderMutex);
			holderAcquired.Set();
		});
		holder.IsBackground = true;
		holder.Start();
		Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));
		holder.Join(TimeSpan.FromSeconds(10));

		using var waiterMutex = new Mutex(initiallyOwned: true, name, out bool createdNew);

		bool acquired = SingleInstanceGate.TryAcquireExistingMutex(waiterMutex, TimeSpan.FromSeconds(10));

		Assert.True(acquired);
	}

	[Fact]
	public void TryAcquireExistingMutex_Throws_WhenMutexIsNull()
	{
		Assert.Throws<ArgumentNullException>(
			() => SingleInstanceGate.TryAcquireExistingMutex(null!, TimeSpan.Zero));
	}
}
