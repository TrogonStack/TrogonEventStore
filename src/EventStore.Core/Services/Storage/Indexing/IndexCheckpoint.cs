using System;
using EventStore.Core.Services.Transport.Common;

namespace EventStore.Core.Services.Storage.Indexing;

public readonly record struct IndexCheckpoint
{
	public long CommitPosition { get; }
	public long PreparePosition { get; }

	public IndexCheckpoint(long commitPosition, long preparePosition)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(commitPosition);
		ArgumentOutOfRangeException.ThrowIfNegative(preparePosition);

		if (commitPosition < preparePosition)
		{
			throw new ArgumentOutOfRangeException(nameof(CommitPosition),
				"The commit position cannot be less than the prepare position.");
		}

		CommitPosition = commitPosition;
		PreparePosition = preparePosition;
	}

	public Position ToPosition() => Position.FromInt64(CommitPosition, PreparePosition);
}
