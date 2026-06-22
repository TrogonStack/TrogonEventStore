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
	async ValueTask IAsyncHandle<ClientMessage.FilteredReadAllEventsForward>.HandleAsync(
		ClientMessage.FilteredReadAllEventsForward msg, CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.FilteredReadAllEventsForwardCompleted(
					msg.CorrelationId, FilteredReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default, default, default));
			}

			Log.Debug(
				"Read All Stream Events Forward Filtered operation has expired for C:{0}/P:{1}. Operation Expired at {2}",
				msg.CommitPosition, msg.PreparePosition, msg.Expires);
			return;
		}

		using var cts = Multiplex(ref token, msg);
		try
		{
			var res = await FilteredReadAllEventsForward(msg, token);
			switch (res.Result)
			{
				case FilteredReadAllResult.Success:
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
				case FilteredReadAllResult.NotModified:
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
				case FilteredReadAllResult.Error:
				case FilteredReadAllResult.AccessDenied:
				case FilteredReadAllResult.InvalidPosition:
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
				msg.Envelope.ReplyWith(new ClientMessage.FilteredReadAllEventsForwardCompleted(
					msg.CorrelationId, FilteredReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default, default, default));
			}

			Log.Debug(
				"Read All Stream Events Forward Filtered operation has expired for C:{0}/P:{1}. Operation Expired at {2}",
				msg.CommitPosition, msg.PreparePosition, msg.Expires);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}
	}

	async ValueTask IAsyncHandle<ClientMessage.FilteredReadAllEventsBackward>.HandleAsync(
		ClientMessage.FilteredReadAllEventsBackward msg, CancellationToken token)
	{
		if (msg.CancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (msg.Expires < DateTime.UtcNow)
		{
			if (msg.ReplyOnExpired)
			{
				msg.Envelope.ReplyWith(new ClientMessage.FilteredReadAllEventsBackwardCompleted(
					msg.CorrelationId, FilteredReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default, default));
			}

			Log.Debug(
				"Read All Stream Events Backward Filtered operation has expired for C:{0}/P:{1}. Operation Expired at {2}",
				msg.CommitPosition, msg.PreparePosition, msg.Expires);
			return;
		}

		using var cts = Multiplex(ref token, msg);
		try
		{
			var res = await FilteredReadAllEventsBackward(msg, token);
			switch (res.Result)
			{
				case FilteredReadAllResult.Success:
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
				case FilteredReadAllResult.NotModified:
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
				case FilteredReadAllResult.Error:
				case FilteredReadAllResult.AccessDenied:
				case FilteredReadAllResult.InvalidPosition:
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
				msg.Envelope.ReplyWith(new ClientMessage.FilteredReadAllEventsBackwardCompleted(
					msg.CorrelationId, FilteredReadAllResult.Expired,
					default, ResolvedEvent.EmptyArray, default, default, default,
					currentPos: new TFPos(msg.CommitPosition, msg.PreparePosition),
					TFPos.Invalid, TFPos.Invalid, default, default));
			}

			Log.Debug(
				"Read All Stream Events Backward Filtered operation has expired for C:{0}/P:{1}. Operation Expired at {2}",
				msg.CommitPosition, msg.PreparePosition, msg.Expires);
		}
		catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
		{
			throw new OperationCanceledException(null, ex, cts.CancellationOrigin);
		}
	}

	private async ValueTask<ClientMessage.FilteredReadAllEventsForwardCompleted> FilteredReadAllEventsForward(
		ClientMessage.FilteredReadAllEventsForward msg, CancellationToken token)
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
				return NoDataForFilteredCommand(msg, FilteredReadAllResult.InvalidPosition, pos, lastIndexedPosition,
					"Invalid position.");
			}

			if (msg.ValidationTfLastCommitPosition == lastIndexedPosition)
			{
				return NoDataForFilteredCommand(msg, FilteredReadAllResult.NotModified, pos,
					lastIndexedPosition);
			}

			var res = await _readIndex.ReadAllEventsForwardFiltered(pos, msg.MaxCount, msg.MaxSearchWindow,
				msg.EventFilter, token);
			if (await ResolveReadAllResult(res.Records, msg.ResolveLinkTos, msg.User, token) is not { } resolved)
			{
				return NoDataForFilteredCommand(msg, FilteredReadAllResult.AccessDenied, pos,
					lastIndexedPosition);
			}

			var metadata = await _readIndex.GetStreamMetadata(_systemStreams.AllStream, token);
			return new ClientMessage.FilteredReadAllEventsForwardCompleted(
				msg.CorrelationId, FilteredReadAllResult.Success, null, resolved, metadata, false,
				msg.MaxCount,
				res.CurrentPos, res.NextPos, res.PrevPos, lastIndexedPosition, res.IsEndOfStream,
				res.ConsideredEventsCount);
		}
		catch (Exception exc) when (exc is InvalidReadException or UnableToReadPastEndOfStreamException)
		{
			Log.Warning(exc,
				"Error during processing ReadAllEventsForwardFiltered request. The read appears to be at an invalid position.");
			return NoDataForFilteredCommand(msg, FilteredReadAllResult.InvalidPosition, pos, lastIndexedPosition,
				exc.Message);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadAllEventsForwardFiltered request.");
			return NoDataForFilteredCommand(msg, FilteredReadAllResult.Error, pos, lastIndexedPosition,
				exc.Message);
		}
	}

	private async ValueTask<ClientMessage.FilteredReadAllEventsBackwardCompleted> FilteredReadAllEventsBackward(
		ClientMessage.FilteredReadAllEventsBackward msg, CancellationToken token)
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
				return NoDataForFilteredCommand(msg, FilteredReadAllResult.InvalidPosition, pos, lastIndexedPosition,
					"Invalid position.");
			}

			if (msg.ValidationTfLastCommitPosition == lastIndexedPosition)
			{
				return NoDataForFilteredCommand(msg, FilteredReadAllResult.NotModified, pos,
					lastIndexedPosition);
			}

			var res = await _readIndex.ReadAllEventsBackwardFiltered(pos, msg.MaxCount, msg.MaxSearchWindow,
				msg.EventFilter, token);
			if (await ResolveReadAllResult(res.Records, msg.ResolveLinkTos, msg.User, token) is not { } resolved)
			{
				return NoDataForFilteredCommand(msg, FilteredReadAllResult.AccessDenied, pos,
					lastIndexedPosition);
			}

			var metadata = await _readIndex.GetStreamMetadata(_systemStreams.AllStream, token);
			return new ClientMessage.FilteredReadAllEventsBackwardCompleted(
				msg.CorrelationId, FilteredReadAllResult.Success, null, resolved, metadata, false,
				msg.MaxCount,
				res.CurrentPos, res.NextPos, res.PrevPos, lastIndexedPosition, res.IsEndOfStream);
		}
		catch (Exception exc) when (exc is InvalidReadException or UnableToReadPastEndOfStreamException)
		{
			Log.Warning(exc,
				"Error during processing ReadAllEventsBackwardFiltered request. The read appears to be at an invalid position.");
			return NoDataForFilteredCommand(msg, FilteredReadAllResult.InvalidPosition, pos, lastIndexedPosition,
				exc.Message);
		}
		catch (Exception exc) when (exc is not OperationCanceledException oce || oce.CancellationToken != token)
		{
			Log.Error(exc, "Error during processing ReadAllEventsBackwardFiltered request.");
			return NoDataForFilteredCommand(msg, FilteredReadAllResult.Error, pos, lastIndexedPosition,
				exc.Message);
		}
	}

	private ClientMessage.FilteredReadAllEventsForwardCompleted NoDataForFilteredCommand(
		ClientMessage.FilteredReadAllEventsForward msg, FilteredReadAllResult result, TFPos pos,
		long lastIndexedPosition, string error = null)
	{
		return new ClientMessage.FilteredReadAllEventsForwardCompleted(
			msg.CorrelationId, result, error, ResolvedEvent.EmptyArray, null, false,
			msg.MaxCount, pos, TFPos.Invalid, TFPos.Invalid, lastIndexedPosition, false, 0L);
	}

	private ClientMessage.FilteredReadAllEventsBackwardCompleted NoDataForFilteredCommand(
		ClientMessage.FilteredReadAllEventsBackward msg, FilteredReadAllResult result, TFPos pos,
		long lastIndexedPosition, string error = null)
	{
		return new ClientMessage.FilteredReadAllEventsBackwardCompleted(
			msg.CorrelationId, result, error, ResolvedEvent.EmptyArray, null, false,
			msg.MaxCount, pos, TFPos.Invalid, TFPos.Invalid, lastIndexedPosition, false);
	}
}
