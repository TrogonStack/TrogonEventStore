namespace EventStore.Core.Data;

public readonly record struct ConsistencyCheckFailure(
	int StreamIndex,
	long ExpectedVersion,
	long CurrentVersion,
	bool? IsSoftDeleted);
