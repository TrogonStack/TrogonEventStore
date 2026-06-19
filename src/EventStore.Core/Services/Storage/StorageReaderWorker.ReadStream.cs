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
	async ValueTask IAsyncHandle<ClientMessage.ReadStreamEventsForward>.HandleAsync(
		ClientMessage.ReadStreamEventsForward msg, CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadStreamEventsForwardCompleted(
					msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount,
					ReadStreamResult.Expired,
					ResolvedEvent.EmptyArray, default, default, default, default, default, default, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read Stream Events Forward operation has expired for Stream: {stream}, From Event Number: {fromEventNumber}, Max Count: {maxCount}. Operation Expired at {expiryDateTime}",
					msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.Expires);
			}

			return;
		}

		ClientMessage.ReadStreamEventsForwardCompleted res;
		using var cts = Multiplex(ref token, msg);
		try
		{
			using var readSlot = await AcquireReadSlot(token);
			res = _virtualStreamReader.CanReadStream(msg.EventStreamId)
				? await _virtualStreamReader.ReadForwards(msg, token)
				: await ReadStreamEventsForward(msg, token);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsTimedOut)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadStreamEventsForwardCompleted(
					msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount,
					ReadStreamResult.Expired,
					ResolvedEvent.EmptyArray, default, default, default, default, default, default, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read Stream Events Forward operation has expired for Stream: {stream}, From Event Number: {fromEventNumber}, Max Count: {maxCount}. Operation Expired at {expiryDateTime}",
					msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.Expires);
			}

			return;
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}

		switch (res.Result)
		{
			case ReadStreamResult.Success:
			case ReadStreamResult.NoStream:
			case ReadStreamResult.NotModified:
				if (msg.LongPollTimeout.HasValue && res.FromEventNumber > res.LastEventNumber)
				{
					_publisher.Publish(new SubscriptionMessage.PollStream(
						msg.EventStreamId, res.TfLastCommitPosition, res.LastEventNumber,
						DateTime.UtcNow + msg.LongPollTimeout.Value, msg));
				}
				else
				{
					msg.Envelope.ReplyWith(res);
				}

				break;
			case ReadStreamResult.StreamDeleted:
			case ReadStreamResult.Error:
			case ReadStreamResult.AccessDenied:
				msg.Envelope.ReplyWith(res);
				break;
			default:
				throw new ArgumentOutOfRangeException(
					$"Unknown ReadStreamResult: {res.Result}");
		}
	}

	async ValueTask IAsyncHandle<ClientMessage.ReadStreamEventsBackward>.HandleAsync(
		ClientMessage.ReadStreamEventsBackward msg, CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadStreamEventsBackwardCompleted(
					msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount,
					ReadStreamResult.Expired,
					ResolvedEvent.EmptyArray, default, default, default, default, default, default, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read Stream Events Backward operation has expired for Stream: {stream}, From Event Number: {fromEventNumber}, Max Count: {maxCount}. Operation Expired at {expiryDateTime}",
					msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.Expires);
			}

			return;
		}

		using var cts = Multiplex(ref token, msg);
		try
		{
			using var readSlot = await AcquireReadSlot(token);
			var res = _virtualStreamReader.CanReadStream(msg.EventStreamId)
				? await _virtualStreamReader.ReadBackwards(msg, token)
				: await ReadStreamEventsBackward(msg, token);

			msg.Envelope.ReplyWith(res);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsTimedOut)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadStreamEventsBackwardCompleted(
					msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount,
					ReadStreamResult.Expired,
					ResolvedEvent.EmptyArray, default, default, default, default, default, default, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read Stream Events Backward operation has expired for Stream: {stream}, From Event Number: {fromEventNumber}, Max Count: {maxCount}. Operation Expired at {expiryDateTime}",
					msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.Expires);
			}
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}
	}

	private async ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadStreamEventsForward(
		ClientMessage.ReadStreamEventsForward msg, CancellationToken token)
	{

		var lastIndexPosition = _readIndex.LastIndexedPosition;
		try
		{
			if (msg.MaxCount > MaxPageSize)
			{
				throw new ArgumentException($"Read size too big, should be less than {MaxPageSize} items");
			}

			var streamName = msg.EventStreamId;
			var streamId = _readIndex.GetStreamId(msg.EventStreamId);
			if (msg.ValidationStreamVersion.HasValue &&
				await _readIndex.GetStreamLastEventNumber(streamId, token) == msg.ValidationStreamVersion)
			{
				return NoData(msg, ReadStreamResult.NotModified, lastIndexPosition,
					msg.ValidationStreamVersion.Value);
			}

			var result =
				await _readIndex.ReadStreamEventsForward(streamName, streamId, msg.FromEventNumber, msg.MaxCount,
					token);
			CheckEventsOrder(msg, result);
			if (await ResolveLinkToEvents(result.Records, msg.ResolveLinkTos, msg.User, token) is not { } resolvedPairs)
			{
				return NoData(msg, ReadStreamResult.AccessDenied, lastIndexPosition);
			}

			return new ClientMessage.ReadStreamEventsForwardCompleted(
				msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount,
				(ReadStreamResult)result.Result, resolvedPairs, result.Metadata, false, string.Empty,
				result.NextEventNumber, result.LastEventNumber, result.IsEndOfStream, lastIndexPosition);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadStreamEventsForward request.");
			return NoData(msg, ReadStreamResult.Error, lastIndexPosition, error: exc.Message);
		}
	}

	private async ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadStreamEventsBackward(
		ClientMessage.ReadStreamEventsBackward msg, CancellationToken token)
	{

		var lastIndexedPosition = _readIndex.LastIndexedPosition;
		try
		{
			if (msg.MaxCount > MaxPageSize)
			{
				throw new ArgumentException($"Read size too big, should be less than {MaxPageSize} items");
			}

			var streamName = msg.EventStreamId;
			var streamId = _readIndex.GetStreamId(msg.EventStreamId);
			if (msg.ValidationStreamVersion.HasValue &&
				await _readIndex.GetStreamLastEventNumber(streamId, token) == msg.ValidationStreamVersion)
			{
				return NoData(msg, ReadStreamResult.NotModified, lastIndexedPosition,
					msg.ValidationStreamVersion.Value);
			}

			var result = await _readIndex.ReadStreamEventsBackward(streamName, streamId, msg.FromEventNumber,
				msg.MaxCount, token);
			CheckEventsOrder(msg, result);
			if (await ResolveLinkToEvents(result.Records, msg.ResolveLinkTos, msg.User, token) is not { } resolvedPairs)
			{
				return NoData(msg, ReadStreamResult.AccessDenied, lastIndexedPosition);
			}

			return new ClientMessage.ReadStreamEventsBackwardCompleted(
				msg.CorrelationId, msg.EventStreamId, result.FromEventNumber, result.MaxCount,
				(ReadStreamResult)result.Result, resolvedPairs, result.Metadata, false, string.Empty,
				result.NextEventNumber, result.LastEventNumber, result.IsEndOfStream, lastIndexedPosition);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadStreamEventsBackward request.");
			return NoData(msg, ReadStreamResult.Error, lastIndexedPosition, error: exc.Message);
		}
	}

	private static ClientMessage.ReadStreamEventsForwardCompleted NoData(ClientMessage.ReadStreamEventsForward msg,
		ReadStreamResult result, long lastIndexedPosition, long lastEventNumber = -1, string error = null)
	{
		return new ClientMessage.ReadStreamEventsForwardCompleted(
			msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, result,
			EmptyRecords, null, false, error ?? string.Empty, -1, lastEventNumber, true, lastIndexedPosition);
	}

	private static ClientMessage.ReadStreamEventsBackwardCompleted NoData(
		ClientMessage.ReadStreamEventsBackward msg, ReadStreamResult result, long lastIndexedPosition,
		long lastEventNumber = -1, string error = null)
	{
		return new ClientMessage.ReadStreamEventsBackwardCompleted(
			msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, result,
			EmptyRecords, null, false, error ?? string.Empty, -1, lastEventNumber, true, lastIndexedPosition);
	}

	private static void CheckEventsOrder(ClientMessage.ReadStreamEventsForward msg, IndexReadStreamResult result)
	{
		for (var index = 1; index < result.Records.Length; index++)
		{
			if (result.Records[index].EventNumber != result.Records[index - 1].EventNumber + 1)
			{
				throw new Exception(
					$"Invalid order of events has been detected in read index for the event stream '{msg.EventStreamId}'. " +
					$"The event {result.Records[index].EventNumber} at position {result.Records[index].LogPosition} goes after the event {result.Records[index - 1].EventNumber} at position {result.Records[index - 1].LogPosition}");
			}
		}
	}

	private static void CheckEventsOrder(ClientMessage.ReadStreamEventsBackward msg, IndexReadStreamResult result)
	{
		for (var index = 1; index < result.Records.Length; index++)
		{
			if (result.Records[index].EventNumber != result.Records[index - 1].EventNumber - 1)
			{
				throw new Exception(
					$"Invalid order of events has been detected in read index for the event stream '{msg.EventStreamId}'. " +
					$"The event {result.Records[index].EventNumber} at position {result.Records[index].LogPosition} goes after the event {result.Records[index - 1].EventNumber} at position {result.Records[index - 1].LogPosition}");
			}
		}
	}
}
