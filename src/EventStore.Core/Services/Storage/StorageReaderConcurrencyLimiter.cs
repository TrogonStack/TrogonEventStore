using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;

#nullable enable

namespace EventStore.Core.Services.Storage;

public sealed class StorageReaderConcurrencyLimiter : IQueueProcessingLimiter
{
	private readonly SemaphoreSlim? _semaphore;

	private StorageReaderConcurrencyLimiter(int maxConcurrentReadRequests)
	{
		MaxConcurrentReadRequests = maxConcurrentReadRequests;
		_semaphore = maxConcurrentReadRequests > 0
			? new SemaphoreSlim(maxConcurrentReadRequests, maxConcurrentReadRequests)
			: null;
	}

	public int MaxConcurrentReadRequests { get; }

	public bool ShouldLimit(Message message) =>
		message is ClientMessage.ReadRequestMessage;

	public static StorageReaderConcurrencyLimiter Create(int maxConcurrentReadRequests)
	{
		if (maxConcurrentReadRequests < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxConcurrentReadRequests),
				maxConcurrentReadRequests,
				$"{nameof(maxConcurrentReadRequests)} must be greater than or equal to 0.");
		}

		return new StorageReaderConcurrencyLimiter(maxConcurrentReadRequests);
	}

	public ValueTask<Lease> Acquire(CancellationToken token)
	{
		if (_semaphore is null)
		{
			return new ValueTask<Lease>(default(Lease));
		}

		return Wait(_semaphore, token);
	}

	private static async ValueTask<Lease> Wait(SemaphoreSlim semaphore, CancellationToken token)
	{
		await semaphore.WaitAsync(token);
		return new Lease(semaphore);
	}

	async ValueTask<IDisposable> IQueueProcessingLimiter.Acquire(CancellationToken token) =>
		await Acquire(token);

	public readonly struct Lease : IDisposable
	{
		private readonly SemaphoreSlim? _semaphore;

		internal Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

		public void Dispose() => _semaphore?.Release();
	}
}
