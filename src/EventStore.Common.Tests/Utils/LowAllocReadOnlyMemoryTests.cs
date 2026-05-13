using EventStore.Common.Utils;

namespace EventStore.Common.Tests.Utils;

public class LowAllocReadOnlyMemoryTests
{
	private static readonly int[] SingleItem = [5];
	private static readonly int[] ManyItems = [3, 4, 5];
	private static readonly int[] ForeachItems = [1, 2, 3];

	[Fact]
	public void Empty_has_no_items()
	{
		var sut = LowAllocReadOnlyMemory<int>.Empty;

		Assert.Equal(0, sut.Length);
		Assert.Throws<InvalidOperationException>(() => sut.Single);
		Assert.True(sut.Span.IsEmpty);
		Assert.Empty(sut.ToArray());

		foreach (var item in sut)
		{
			Assert.Fail($"Unexpected item: {item}");
		}
	}

	[Fact]
	public void Single_item_is_available_without_backing_memory()
	{
		var sut = new LowAllocReadOnlyMemory<int>(5);

		Assert.Equal(1, sut.Length);
		Assert.Equal(5, sut.Single);
		Assert.True(SingleItem.AsSpan().SequenceEqual(sut.Span));
		Assert.Equal(SingleItem, sut.ToArray());
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void Read_only_memory_constructor_preserves_items(int length)
	{
		var expected = Enumerable.Range(0, length).ToArray();
		var sut = new LowAllocReadOnlyMemory<int>(expected);

		Assert.Equal(expected.Length, sut.Length);
		Assert.True(expected.AsSpan().SequenceEqual(sut.Span));
		Assert.Equal(expected, sut.ToArray());

		if (length == 1)
		{
			Assert.Equal(expected[0], sut.Single);
		}
		else
		{
			Assert.Throws<InvalidOperationException>(() => sut.Single);
		}
	}

	[Fact]
	public void Collection_expressions_build_the_value()
	{
		LowAllocReadOnlyMemory<int> empty = [];
		LowAllocReadOnlyMemory<int> single = [2];
		LowAllocReadOnlyMemory<int> many = [3, 4, 5];

		Assert.Equal(0, empty.Length);
		Assert.Equal(2, single.Single);
		Assert.True(ManyItems.AsSpan().SequenceEqual(many.Span));
	}

	[Fact]
	public void Foreach_reads_items_in_order()
	{
		LowAllocReadOnlyMemory<int> sut = [1, 2, 3];
		var actual = new List<int>();

		foreach (var item in sut)
		{
			actual.Add(item);
		}

		Assert.Equal(ForeachItems, actual);
	}
}
