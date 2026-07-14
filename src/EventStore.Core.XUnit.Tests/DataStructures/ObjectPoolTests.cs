using EventStore.Core.DataStructures;
using Xunit;

namespace EventStore.Core.XUnit.Tests.DataStructures;

public class ObjectPoolTests
{
	[Fact]
	public void creates_new_object_on_each_request_until_max_count()
	{
		var id = 0;
		using var pool = new ObjectPool<int>(
			objectPoolName: "test",
			initialCount: 0,
			maxCount: 3,
			factory: () => ++id);

		Assert.Equal(1, pool.Get());
		Assert.Equal(2, pool.Get());
		Assert.Equal(3, pool.Get());
	}

	[Fact]
	public void reuses_returned_objects()
	{
		var id = 0;
		using var pool = new ObjectPool<int>(
			objectPoolName: "test",
			initialCount: 0,
			maxCount: 3,
			factory: () => ++id);

		Assert.Equal(1, pool.Get());
		pool.Return(1);
		Assert.Equal(1, pool.Get());

		Assert.Equal(2, pool.Get());
		pool.Return(1);
		Assert.Equal(1, pool.Get());

		Assert.Equal(2, id);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void disposes_all_leased_objects_after_max_count_overflow(bool markForDisposalBeforeReturn)
	{
		var disposedObjects = 0;
		var disposedPools = 0;

		using (var pool = new ObjectPool<int>(
			objectPoolName: "test",
			initialCount: 0,
			maxCount: 3,
			factory: () => 0,
			dispose: _ => disposedObjects++,
			onPoolDisposed: _ => disposedPools++))
		{
			var first = pool.Get();
			var second = pool.Get();
			var third = pool.Get();

			Assert.Throws<ObjectPoolMaxLimitReachedException>(() => pool.Get());

			if (markForDisposalBeforeReturn)
			{
				pool.MarkForDisposal();
			}

			pool.Return(first);
			pool.Return(second);
			pool.Return(third);

			if (!markForDisposalBeforeReturn)
			{
				pool.MarkForDisposal();
			}
		}

		Assert.Equal(3, disposedObjects);
		Assert.Equal(1, disposedPools);
	}
}
