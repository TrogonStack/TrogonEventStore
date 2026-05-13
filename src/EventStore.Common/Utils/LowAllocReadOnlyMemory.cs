using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EventStore.Common.Utils;

[CollectionBuilder(typeof(LowAllocReadOnlyMemory), nameof(LowAllocReadOnlyMemory.Create))]
public readonly struct LowAllocReadOnlyMemory<T>
{
	private readonly ReadOnlyMemory<T> _many;
	private readonly T _single;
	private readonly bool _hasSingle;

	public LowAllocReadOnlyMemory(T item)
	{
		_single = item;
		_hasSingle = true;
	}

	public LowAllocReadOnlyMemory(ReadOnlyMemory<T> items)
	{
		if (items.Span is [var item])
		{
			_single = item;
			_hasSingle = true;
			return;
		}

		_many = items;
	}

	public static LowAllocReadOnlyMemory<T> Empty => default;

	public static implicit operator LowAllocReadOnlyMemory<T>(T[] items) => new(items);

	public int Length => _hasSingle ? 1 : _many.Length;

	public T Single => _hasSingle
		? _single
		: throw new InvalidOperationException($"Cannot read a single item from a collection of length {Length}.");

	public ReadOnlySpan<T> Span => _hasSingle
		? MemoryMarshal.CreateReadOnlySpan(in _single, 1)
		: _many.Span;

	public ReadOnlySpan<T>.Enumerator GetEnumerator() => Span.GetEnumerator();

	public T[] ToArray() => Span.ToArray();
}

public static class LowAllocReadOnlyMemory
{
	public static LowAllocReadOnlyMemory<T> Create<T>(ReadOnlySpan<T> items) =>
		items is [var item]
			? new LowAllocReadOnlyMemory<T>(item)
			: new LowAllocReadOnlyMemory<T>(items.ToArray());
}
