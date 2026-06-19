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
	async ValueTask IAsyncHandle<ClientMessage.ReadAllEventsForward>.HandleAsync(ClientMessage.ReadAllEventsForward msg,
		CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadAllEventsForwardCompleted(
					msg.CorrelationId, ReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read All Stream Events Forward operation has expired for C:{commitPosition}/P:{preparePosition}. Operation Expired at {expiryDateTime}",
					msg.CommitPosition, msg.PreparePosition, msg.Expires);
			}

			return;
		}

		using var cts = Multiplex(ref token, msg);
		try
		{
			using var readSlot = await AcquireReadSlot(token);
			var res = await ReadAllEventsForward(msg, token);
			switch (res.Result)
			{
				case ReadAllResult.Success:
					if (msg.LongPollTimeout.HasValue && res.IsEndOfStream && res.Events.Count is 0)
					{
						_publisher.Publish(new SubscriptionMessage.PollStream(
							SubscriptionsService.AllStreamsSubscriptionId, res.TfLastCommitPosition, null,
							DateTime.UtcNow + msg.LongPollTimeout.Value, msg));
					}
					else
					{
						msg.Envelope.ReplyWith(res);
					}

					break;
				case ReadAllResult.NotModified:
					if (msg.LongPollTimeout.HasValue && res.IsEndOfStream &&
						res.CurrentPos.CommitPosition > res.TfLastCommitPosition)
					{
						_publisher.Publish(new SubscriptionMessage.PollStream(
							SubscriptionsService.AllStreamsSubscriptionId, res.TfLastCommitPosition, null,
							DateTime.UtcNow + msg.LongPollTimeout.Value, msg));
					}
					else
					{
						msg.Envelope.ReplyWith(res);
					}

					break;
				case ReadAllResult.Error:
				case ReadAllResult.AccessDenied:
				case ReadAllResult.InvalidPosition:
					msg.Envelope.ReplyWith(res);
					break;
				default:
					throw new ArgumentOutOfRangeException($"Unknown ReadAllResult: {res.Result}");
			}
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsTimedOut)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadAllEventsForwardCompleted(
					msg.CorrelationId, ReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read All Stream Events Forward operation has expired for C:{commitPosition}/P:{preparePosition}. Operation Expired at {expiryDateTime}",
					msg.CommitPosition, msg.PreparePosition, msg.Expires);
			}
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}
	}

	async ValueTask IAsyncHandle<ClientMessage.ReadAllEventsBackward>.HandleAsync(
		ClientMessage.ReadAllEventsBackward msg, CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadAllEventsBackwardCompleted(
					msg.CorrelationId, ReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read All Stream Events Backward operation has expired for C:{commitPosition}/P:{preparePosition}. Operation Expired at {expiryDateTime}",
					msg.CommitPosition, msg.PreparePosition, msg.Expires);
			}

			return;
		}

		using var cts = Multiplex(ref token, msg);
		try
		{
			using var readSlot = await AcquireReadSlot(token);
			msg.Envelope.ReplyWith(await ReadAllEventsBackward(msg, token));
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsTimedOut)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.ReadAllEventsBackwardCompleted(
					msg.CorrelationId, ReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default));
			}

			if (LogExpiredMessage(msg.Expires))
			{
				Log.Debug(
					"Read All Stream Events Backward operation has expired for C:{commitPosition}/P:{preparePosition}. Operation Expired at {expiryDateTime}",
					msg.CommitPosition, msg.PreparePosition, msg.Expires);
			}
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}
	}

	private async ValueTask<ClientMessage.ReadAllEventsForwardCompleted>
		ReadAllEventsForward(ClientMessage.ReadAllEventsForward msg, CancellationToken token)
	{

		var pos = new TFPos(msg.CommitPosition, msg.PreparePosition);
		var lastIndexedPosition = _readIndex.LastIndexedPosition;
		try
		{
			if (msg.MaxCount > MaxPageSize)
			{
				throw new ArgumentException($"Read size too big, should be less than {MaxPageSize} items");
			}

			if (pos == TFPos.HeadOfTf)
			{
				var checkpoint = _writerCheckpoint.Read();
				pos = new TFPos(checkpoint, checkpoint);
			}

			if (pos.CommitPosition < 0 || pos.PreparePosition < 0)
			{
				return NoData(msg, ReadAllResult.InvalidPosition, pos, lastIndexedPosition, "Invalid position.");
			}

			if (msg.ValidationTfLastCommitPosition == lastIndexedPosition)
			{
				return NoData(msg, ReadAllResult.NotModified, pos, lastIndexedPosition);
			}

			var res = await _readIndex.ReadAllEventsForward(pos, msg.MaxCount, token);
			if (await ResolveReadAllResult(res.Records, msg.ResolveLinkTos, msg.User, token) is not { } resolved)
			{
				return NoData(msg, ReadAllResult.AccessDenied, pos, lastIndexedPosition);
			}

			var metadata = await _readIndex.GetStreamMetadata(_systemStreams.AllStream, token);
			return new ClientMessage.ReadAllEventsForwardCompleted(
				msg.CorrelationId, ReadAllResult.Success, null, resolved, metadata, false, msg.MaxCount,
				res.CurrentPos, res.NextPos, res.PrevPos, lastIndexedPosition);
		}
		catch (Exception exc) when (exc is InvalidReadException or UnableToReadPastEndOfStreamException)
		{
			Log.Warning(exc,
				"Error during processing ReadAllEventsBackward request. The read appears to be at an invalid position.");
			return NoData(msg, ReadAllResult.InvalidPosition, pos, lastIndexedPosition, exc.Message);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadAllEventsForward request.");
			return NoData(msg, ReadAllResult.Error, pos, lastIndexedPosition, exc.Message);
		}
	}

	private async ValueTask<ClientMessage.ReadAllEventsBackwardCompleted> ReadAllEventsBackward(
		ClientMessage.ReadAllEventsBackward msg, CancellationToken token)
	{

		var pos = new TFPos(msg.CommitPosition, msg.PreparePosition);
		var lastIndexedPosition = _readIndex.LastIndexedPosition;
		try
		{
			if (msg.MaxCount > MaxPageSize)
			{
				throw new ArgumentException($"Read size too big, should be less than {MaxPageSize} items");
			}

			if (pos == TFPos.HeadOfTf)
			{
				var checkpoint = _writerCheckpoint.Read();
				pos = new TFPos(checkpoint, checkpoint);
			}

			if (pos.CommitPosition < 0 || pos.PreparePosition < 0)
			{
				return NoData(msg, ReadAllResult.InvalidPosition, pos, lastIndexedPosition, "Invalid position.");
			}

			if (msg.ValidationTfLastCommitPosition == lastIndexedPosition)
			{
				return NoData(msg, ReadAllResult.NotModified, pos, lastIndexedPosition);
			}

			var res = await _readIndex.ReadAllEventsBackward(pos, msg.MaxCount, token);
			if (await ResolveReadAllResult(res.Records, msg.ResolveLinkTos, msg.User, token) is not { } resolved)
			{
				return NoData(msg, ReadAllResult.AccessDenied, pos, lastIndexedPosition);
			}

			var metadata = await _readIndex.GetStreamMetadata(_systemStreams.AllStream, token);
			return new ClientMessage.ReadAllEventsBackwardCompleted(
				msg.CorrelationId, ReadAllResult.Success, null, resolved, metadata, false, msg.MaxCount,
				res.CurrentPos, res.NextPos, res.PrevPos, lastIndexedPosition);
		}
		catch (Exception exc) when (exc is InvalidReadException or UnableToReadPastEndOfStreamException)
		{
			Log.Warning(exc,
				"Error during processing ReadAllEventsBackward request. The read appears to be at an invalid position.");
			return NoData(msg, ReadAllResult.InvalidPosition, pos, lastIndexedPosition, exc.Message);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadAllEventsBackward request.");
			return NoData(msg, ReadAllResult.Error, pos, lastIndexedPosition, exc.Message);
		}
	}

	private ClientMessage.ReadAllEventsForwardCompleted NoData(ClientMessage.ReadAllEventsForward msg,
		ReadAllResult result, TFPos pos, long lastIndexedPosition, string error = null)
	{
		return new ClientMessage.ReadAllEventsForwardCompleted(
			msg.CorrelationId, result, error, ResolvedEvent.EmptyArray, null, false,
			msg.MaxCount, pos, TFPos.Invalid, TFPos.Invalid, lastIndexedPosition);
	}

	private ClientMessage.ReadAllEventsBackwardCompleted NoData(ClientMessage.ReadAllEventsBackward msg,
		ReadAllResult result, TFPos pos, long lastIndexedPosition, string error = null)
	{
		return new ClientMessage.ReadAllEventsBackwardCompleted(
			msg.CorrelationId, result, error, ResolvedEvent.EmptyArray, null, false,
			msg.MaxCount, pos, TFPos.Invalid, TFPos.Invalid, lastIndexedPosition);
	}
}
