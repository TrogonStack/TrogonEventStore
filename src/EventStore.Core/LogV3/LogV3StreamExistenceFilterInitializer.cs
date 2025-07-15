using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.LogAbstraction;
using StreamId = System.UInt32;

namespace EventStore.Core.LogV3;

/// Populates a stream existence filter by iterating through the names
/// In V3 the the bloom filter checkpoint is the last processed stream number.
public class LogV3StreamExistenceFilterInitializer(INameLookup<StreamId> streamNames) : INameExistenceFilterInitializer
{
	public async ValueTask Initialize(INameExistenceFilter filter, long truncateToPosition, CancellationToken token)
	{
		// todo: truncate if necessary. implementation will likely depend on how the indexes come out

		if (!(await streamNames.TryGetLastValue(token)).TryGet(out var sourceLastStreamId))
			return;

		var startStreamId = (uint)Math.Max(LogV3SystemStreams.FirstRealStream, filter.CurrentCheckpoint);
		for (var streamId = startStreamId;
		     streamId <= sourceLastStreamId;
		     streamId += LogV3SystemStreams.StreamInterval)
		{
			if (await streamNames.LookupName(streamId, token) is not { } name)
				throw new Exception(
					$"NameExistenceFilter: this should never happen. could not find {streamId} in source");

			filter.Add(name);
			filter.CurrentCheckpoint = streamId;
		}
	}
}
