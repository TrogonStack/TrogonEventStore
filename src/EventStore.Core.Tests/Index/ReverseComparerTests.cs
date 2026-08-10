using System;
using EventStore.Core.Index;
using NUnit.Framework;

namespace EventStore.Core.Tests.Index;

[TestFixture]
public class ReverseComparerTests
{
	[Test]
	public void larger_values_return_as_lower()
	{
		Assert.AreEqual(-1, new ReverseComparer<int>().Compare(5, 3));
	}

	[Test]
	public void smaller_values_return_as_higher()
	{
		Assert.AreEqual(1, new ReverseComparer<int>().Compare(3, 5));
	}

	[Test]
	public void same_values_are_equal()
	{
		Assert.AreEqual(0, new ReverseComparer<int>().Compare(5, 5));
	}

	[Test]
	public void comparing_value_types_does_not_allocate_per_comparison()
	{
		const int maximumOneTimeAllocation = 24;
		var comparer = new ReverseComparer<ulong>();
		_ = comparer.Compare(2, 1);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var result = 0;
		for (ulong i = 0; i < 1_000; i++)
		{
			result += comparer.Compare(i + 1, i);
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		GC.KeepAlive(result);
		Assert.That(allocated, Is.LessThanOrEqualTo(maximumOneTimeAllocation));
	}
}
