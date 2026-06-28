using System;
using EventStore.Core.Services;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed record IndexStreamId
{
	public string Value { get; }

	public IndexStreamId(string value)
	{
		if (SystemStreams.IsInvalidStream(value))
		{
			throw new ArgumentException("Index stream id must be a valid stream id.", nameof(value));
		}

		if (SystemStreams.IsMetastream(value))
		{
			throw new ArgumentException("Index stream id cannot be a metastream id.", nameof(value));
		}

		Value = value;
	}

	public override string ToString() => Value;
}
