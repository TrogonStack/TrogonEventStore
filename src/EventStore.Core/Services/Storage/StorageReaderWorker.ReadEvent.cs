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

public partial class StorageReaderWorker<TStreamId>
{
	async ValueTask IAsyncHandle<ClientMessage.ReadEvent>.HandleAsync(ClientMessage.ReadEvent msg,
		CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read Event operation has expired for Stream: {stream}, Event Number: {eventNumber}. Operation Expired at {expiryDateTime}",
					msg.EventStreamId, msg.EventNumber, msg.Expires);
			}

			return;
		}

		using var cts = Multiplex(ref token, msg);
		try
		{
			var ev = await ReadEvent(msg, token);
			msg.Envelope.ReplyWith(ev);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsTimedOut)
		{
			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read Event operation has expired for Stream: {stream}, Event Number: {eventNumber}. Operation Expired at {expiryDateTime}",
					msg.EventStreamId, msg.EventNumber, msg.Expires);
			}
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}
	}

	private async ValueTask<ClientMessage.ReadEventCompleted> ReadEvent(ClientMessage.ReadEvent msg,
		CancellationToken token)
	{
		try
		{
			var streamName = msg.EventStreamId;
			var streamId = _readIndex.GetStreamId(streamName);
			var result = await _readIndex.ReadEvent(streamName, streamId, msg.EventNumber, token);
			var record = result.Result is ReadEventResult.Success && msg.ResolveLinkTos
				? await ResolveLinkToEvent(result.Record, msg.User, null, token)
				: ResolvedEvent.ForUnresolvedEvent(result.Record);
			if (record is null)
			{
				return NoData(msg, ReadEventResult.AccessDenied);
			}

			if (result.Result is ReadEventResult.NoStream or ReadEventResult.NotFound &&
				_systemStreams.IsMetaStream(streamId) &&
				result.OriginalStreamExists.HasValue &&
				result.OriginalStreamExists.Value)
			{
				return NoData(msg, ReadEventResult.Success);
			}

			return new ClientMessage.ReadEventCompleted(msg.CorrelationId, msg.EventStreamId, result.Result,
				record.Value, result.Metadata, false, null);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadEvent request.");
			return NoData(msg, ReadEventResult.Error, exc.Message);
		}
	}

	private static ClientMessage.ReadEventCompleted NoData(ClientMessage.ReadEvent msg, ReadEventResult result,
		string error = null)
	{
		return new ClientMessage.ReadEventCompleted(msg.CorrelationId, msg.EventStreamId, result,
			ResolvedEvent.EmptyEvent, null, false, error);
	}
}
