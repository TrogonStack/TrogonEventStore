using System;
using System.Collections.Generic;
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

	public readonly struct Builder
	{
		private readonly ReadOnlyMemory<T> _many;
		private readonly T _single;
		private readonly bool _hasSingle;

		public Builder(T item)
		{
			_single = item;
			_hasSingle = true;
		}

		public Builder(ReadOnlyMemory<T> items)
		{
			if (items.Span is [var item])
			{
				_single = item;
				_hasSingle = true;
				return;
			}

			_many = items;
		}

		public static Builder Empty => default;

		public int Count => _hasSingle ? 1 : _many.Length;

		public Builder Add(T item)
		{
			if (_hasSingle)
			{
				return new Builder(new[] { _single, item });
			}

			if (_many.IsEmpty)
			{
				return new Builder(item);
			}

			var items = new T[_many.Length + 1];
			_many.CopyTo(items);
			items[^1] = item;
			return new Builder(items);
		}

		public LowAllocReadOnlyMemory<T> Build() => _hasSingle
			? new LowAllocReadOnlyMemory<T>(_single)
			: new LowAllocReadOnlyMemory<T>(_many);
	}
}

public static class LowAllocReadOnlyMemory
{
	public static LowAllocReadOnlyMemory<T> Create<T>(ReadOnlySpan<T> items) =>
		items switch
		{
			[] => LowAllocReadOnlyMemory<T>.Empty,
			[var item] => new LowAllocReadOnlyMemory<T>(item),
			_ => new LowAllocReadOnlyMemory<T>(items.ToArray())
		};

	public static LowAllocReadOnlyMemory<T> ToLowAllocReadOnlyMemory<T>(this IList<T> items)
	{
		if (items is null || items.Count == 0)
		{
			return LowAllocReadOnlyMemory<T>.Empty;
		}

		if (items.Count == 1)
		{
			return new LowAllocReadOnlyMemory<T>(items[0]);
		}

		var array = new T[items.Count];
		items.CopyTo(array, 0);
		return new LowAllocReadOnlyMemory<T>(array);
	}
}
