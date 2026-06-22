using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Threading;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Exceptions;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.InMemory;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.TimerService;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using ILogger = Serilog.ILogger;
using ReadStreamResult = EventStore.Core.Data.ReadStreamResult;

namespace EventStore.Core.Services.Storage;

public abstract class StorageReaderWorker
{
	protected static readonly ILogger Log = Serilog.Log.ForContext<StorageReaderWorker>();
}

public partial class StorageReaderWorker<TStreamId> :
	StorageReaderWorker,
	IAsyncHandle<ClientMessage.ReadEvent>,
	IAsyncHandle<ClientMessage.ReadStreamEventsBackward>,
	IAsyncHandle<ClientMessage.ReadStreamEventsForward>,
	IAsyncHandle<ClientMessage.ReadAllEventsForward>,
	IAsyncHandle<ClientMessage.ReadAllEventsBackward>,
	IAsyncHandle<ClientMessage.FilteredReadAllEventsForward>,
	IAsyncHandle<ClientMessage.FilteredReadAllEventsBackward>,
	IAsyncHandle<StorageMessage.EffectiveStreamAclRequest>,
	IAsyncHandle<StorageMessage.StreamIdFromTransactionIdRequest>,
	IHandle<StorageMessage.BatchLogExpiredMessages>
{
	private static readonly ResolvedEvent[] EmptyRecords = new ResolvedEvent[0];

	private readonly IPublisher _publisher;
	private readonly IReadIndex<TStreamId> _readIndex;
	private readonly ISystemStreamLookup<TStreamId> _systemStreams;
	private readonly IReadOnlyCheckpoint _writerCheckpoint;
	private readonly IVirtualStreamReader _virtualStreamReader;
	private readonly int _queueId;
	private readonly CancellationTokenMultiplexer _tokenMultiplexer = new();
	private static readonly char[] LinkToSeparator = { '@' };
	private const int MaxPageSize = 4096;
	private DateTime? _lastExpireTime;
	private long _expiredBatchCount;
	private bool _batchLoggingEnabled;

	public StorageReaderWorker(
		IPublisher publisher,
		IReadIndex<TStreamId> readIndex,
		ISystemStreamLookup<TStreamId> systemStreams,
		IReadOnlyCheckpoint writerCheckpoint,
		IVirtualStreamReader virtualStreamReader,
		int queueId)
	{
		Ensure.NotNull(publisher, "publisher");
		Ensure.NotNull(readIndex, "readIndex");
		Ensure.NotNull(systemStreams, nameof(systemStreams));
		Ensure.NotNull(writerCheckpoint, "writerCheckpoint");

		_publisher = publisher;
		_readIndex = readIndex;
		_systemStreams = systemStreams;
		_writerCheckpoint = writerCheckpoint;
		_queueId = queueId;
		_virtualStreamReader = virtualStreamReader;
	}


	async ValueTask IAsyncHandle<StorageMessage.EffectiveStreamAclRequest>.HandleAsync(
		StorageMessage.EffectiveStreamAclRequest msg, CancellationToken token)
	{
		Message reply;
		using var cts = Multiplex(ref token, msg.CancellationToken);

		try
		{
			var acl = await _readIndex.GetEffectiveAcl(_readIndex.GetStreamId(msg.StreamId), token);
			reply = new StorageMessage.EffectiveStreamAclResponse(acl);
		}
		catch (OperationCanceledException e) when (e.CausedBy(cts, msg.CancellationToken))
		{
			reply = new StorageMessage.OperationCancelledMessage(msg.CancellationToken);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}

		msg.Envelope.ReplyWith(reply);
	}

	private async ValueTask<IReadOnlyList<ResolvedEvent>> ResolveLinkToEvents(IReadOnlyList<EventRecord> records,
		bool resolveLinks, ClaimsPrincipal user, CancellationToken token)
	{
		var resolved = new ResolvedEvent[records.Count];
		if (resolveLinks)
		{
			for (var i = 0; i < records.Count; i++)
			{
				if (await ResolveLinkToEvent(records[i], user, null, token) is not { } rec)
				{
					return null;
				}

				resolved[i] = rec;
			}
		}
		else
		{
			for (int i = 0; i < records.Count; ++i)
			{
				resolved[i] = ResolvedEvent.ForUnresolvedEvent(records[i]);
			}
		}

		return resolved;
	}

	private async ValueTask<ResolvedEvent?> ResolveLinkToEvent(EventRecord eventRecord, ClaimsPrincipal user,
		long? commitPosition, CancellationToken token)
	{
		if (eventRecord.EventType is SystemEventTypes.LinkTo)
		{
			try
			{
				var linkPayload = Helper.UTF8NoBom.GetString(eventRecord.Data.Span);
				var parts = linkPayload.Split(LinkToSeparator, 2);
				if (long.TryParse(parts[0], out long eventNumber))
				{
					var streamName = parts[1];
					var streamId = _readIndex.GetStreamId(streamName);
					var res = await _readIndex.ReadEvent(streamName, streamId, eventNumber, token);
					if (res.Result is ReadEventResult.Success)
					{
						return ResolvedEvent.ForResolvedLink(res.Record, eventRecord, commitPosition);
					}

					return ResolvedEvent.ForFailedResolvedLink(eventRecord, res.Result, commitPosition);
				}

				Log.Warning($"Invalid link event payload [{linkPayload}]: {eventRecord}");
				return ResolvedEvent.ForUnresolvedEvent(eventRecord, commitPosition);
			}
			catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
			{
				Log.Error(exc, "Error while resolving link for event record: {eventRecord}",
					eventRecord.ToString());
			}

			// return unresolved link
			return ResolvedEvent.ForFailedResolvedLink(eventRecord, ReadEventResult.Error, commitPosition);
		}

		return ResolvedEvent.ForUnresolvedEvent(eventRecord, commitPosition);
	}

	private async ValueTask<IReadOnlyList<ResolvedEvent>> ResolveReadAllResult(IReadOnlyList<CommitEventRecord> records,
		bool resolveLinks,
		ClaimsPrincipal user, CancellationToken token)
	{
		var result = new ResolvedEvent[records.Count];
		if (resolveLinks)
		{
			for (var i = 0; i < result.Length; ++i)
			{
				var record = records[i];
				if (await ResolveLinkToEvent(record.Event, user, record.CommitPosition, token) is not { } resolvedPair)
				{
					return null;
				}

				result[i] = resolvedPair;
			}
		}
		else
		{
			for (var i = 0; i < result.Length; ++i)
			{
				result[i] = ResolvedEvent.ForUnresolvedEvent(records[i].Event, records[i].CommitPosition);
			}
		}

		return result;
	}

	public void Handle(StorageMessage.BatchLogExpiredMessages message)
	{
		if (!_batchLoggingEnabled)
		{
			return;
		}

		if (_expiredBatchCount == 0)
		{
			_batchLoggingEnabled = false;
			Log.Warning("StorageReaderWorker #{0}: Batch logging disabled, read load is back to normal", _queueId);
			return;
		}

		Log.Warning("StorageReaderWorker #{0}: {1} read operations have expired", _queueId, _expiredBatchCount);
		_expiredBatchCount = 0;
		_publisher.Publish(
			TimerMessage.Schedule.Create(TimeSpan.FromSeconds(2),
				_publisher,
				new StorageMessage.BatchLogExpiredMessages(Guid.NewGuid(), _queueId))
		);
	}

	private bool LogExpiredMessage(DateTime expire)
	{
		if (!_lastExpireTime.HasValue)
		{
			_expiredBatchCount = 1;
			_lastExpireTime = expire;
			return true;
		}

		if (!_batchLoggingEnabled)
		{
			_expiredBatchCount++;
			if (_expiredBatchCount >= 50)
			{
				if (expire - _lastExpireTime.Value <= TimeSpan.FromSeconds(1))
				{
					//heuristic to match approximately >= 50 expired messages / second
					_batchLoggingEnabled = true;
					Log.Warning(
						"StorageReaderWorker #{0}: Batch logging enabled, high rate of expired read messages detected",
						_queueId);
					_publisher.Publish(
						TimerMessage.Schedule.Create(TimeSpan.FromSeconds(2),
							_publisher,
							new StorageMessage.BatchLogExpiredMessages(Guid.NewGuid(), _queueId))
					);
					_expiredBatchCount = 1;
					_lastExpireTime = expire;
					return false;
				}
				else
				{
					_expiredBatchCount = 1;
					_lastExpireTime = expire;
				}
			}

			return true;
		}
		else
		{
			_expiredBatchCount++;
			_lastExpireTime = expire;
			return false;
		}
	}

	async ValueTask IAsyncHandle<StorageMessage.StreamIdFromTransactionIdRequest>.HandleAsync(
		StorageMessage.StreamIdFromTransactionIdRequest message, CancellationToken token)
	{
		using var cts = Multiplex(ref token, message.CancellationToken);
		Message reply;
		try
		{
			var streamId = await _readIndex.GetEventStreamIdByTransactionId(message.TransactionId, token);
			var streamName = await _readIndex.GetStreamName(streamId, token);
			reply = new StorageMessage.StreamIdFromTransactionIdResponse(streamName);
		}
		catch (OperationCanceledException e) when (e.CausedBy(cts, message.CancellationToken))
		{
			reply = new StorageMessage.OperationCancelledMessage(message.CancellationToken);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}

		message.Envelope.ReplyWith(reply);
	}

	private CancellationTokenMultiplexer.Scope Multiplex(
		ref CancellationToken token,
		CancellationToken cancellationToken)
	{
		var scope = _tokenMultiplexer.Combine([token, cancellationToken]);
		token = scope.Token;
		return scope;
	}

	private CancellationTokenMultiplexer.Scope Multiplex(
		ref CancellationToken token,
		ClientMessage.ReadRequestMessage message)
	{
		var scope = message.CanExpire
			? _tokenMultiplexer.Combine(message.Lifetime, [token, message.CancellationToken])
			: _tokenMultiplexer.Combine([token, message.CancellationToken]);

		token = scope.Token;
		return scope;
	}
}
