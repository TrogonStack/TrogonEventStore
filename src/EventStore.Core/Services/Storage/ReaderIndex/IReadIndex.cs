using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.ReaderIndex;

public interface IReadIndex
{
	long LastIndexedPosition { get; }

	ReadIndexStats GetStatistics();

	/// <summary>
	/// Returns event records in the sequence they were committed into TF.
	/// Positions is specified as pre-positions (pointer at the beginning of the record).
	/// </summary>
	IndexReadAllResult ReadAllEventsForward(TFPos pos, int maxCount);

	/// <summary>
	/// Returns event records in the reverse sequence they were committed into TF.
	/// Positions is specified as post-positions (pointer after the end of record).
	/// </summary>
	ValueTask<IndexReadAllResult> ReadAllEventsBackward(TFPos pos, int maxCount, CancellationToken token);

	/// <summary>
	/// Returns event records whose eventType matches the given EventFilter in the sequence they were committed into TF.
	/// Positions is specified as pre-positions (pointer at the beginning of the record).
	/// </summary>
	IndexReadAllResult ReadAllEventsForwardFiltered(TFPos pos, int maxCount, int maxSearchWindow,
		IEventFilter eventFilter);

	/// <summary>
	/// Returns event records whose eventType matches the given EventFilter in the sequence they were committed into TF.
	/// Positions is specified as pre-positions (pointer at the beginning of the record).
	/// </summary>
	ValueTask<IndexReadAllResult> ReadAllEventsBackwardFiltered(TFPos pos, int maxCount, int maxSearchWindow,
		IEventFilter eventFilter, CancellationToken token);

	void Close();
	void Dispose();
}

public interface IReadIndex<TStreamId> : IReadIndex
{
	IIndexWriter<TStreamId> IndexWriter { get; }

	// ReadEvent() / ReadStreamEvents*() :
	// - deleted events are filtered out
	// - duplicates are removed, keeping only the earliest event in the log
	// - streamId drives the read, streamName is only for populating on the result.
	//   this was less messy than safely adding the streamName to the EventRecord at some point after construction.
	IndexReadEventResult ReadEvent(string streamName, TStreamId streamId, long eventNumber);

	IndexReadStreamResult ReadStreamEventsBackward(string streamName, TStreamId streamId, long fromEventNumber,
		int maxCount);

	IndexReadStreamResult ReadStreamEventsForward(string streamName, TStreamId streamId, long fromEventNumber,
		int maxCount);

	// ReadEventInfo_KeepDuplicates() :
	// - deleted events are not filtered out
	// - duplicates are kept, in ascending order of log position
	// - next event number is always -1
	IndexReadEventInfoResult ReadEventInfo_KeepDuplicates(TStreamId streamId, long eventNumber);

	// ReadEventInfo*Collisions() :
	// - deleted events are not filtered out
	// - duplicates are removed, keeping only the earliest event in the log
	// - only events that are before "beforePosition" in the transaction log are returned
	IndexReadEventInfoResult ReadEventInfoForward_KnownCollisions(TStreamId streamId, long fromEventNumber,
		int maxCount, long beforePosition);

	IndexReadEventInfoResult ReadEventInfoForward_NoCollisions(ulong stream, long fromEventNumber, int maxCount,
		long beforePosition);

	IndexReadEventInfoResult ReadEventInfoBackward_KnownCollisions(TStreamId streamId, long fromEventNumber,
		int maxCount, long beforePosition);

	IndexReadEventInfoResult ReadEventInfoBackward_NoCollisions(ulong stream, Func<ulong, TStreamId> getStreamId,
		long fromEventNumber, int maxCount, long beforePosition);

	bool IsStreamDeleted(TStreamId streamId);
	long GetStreamLastEventNumber(TStreamId streamId);
	long GetStreamLastEventNumber_KnownCollisions(TStreamId streamId, long beforePosition);
	long GetStreamLastEventNumber_NoCollisions(ulong stream, Func<ulong, TStreamId> getStreamId, long beforePosition);
	StreamMetadata GetStreamMetadata(TStreamId streamId);
	StorageMessage.EffectiveAcl GetEffectiveAcl(TStreamId streamId);
	TStreamId GetEventStreamIdByTransactionId(long transactionId);

	TStreamId GetStreamId(string streamName);
	string GetStreamName(TStreamId streamId);
}
