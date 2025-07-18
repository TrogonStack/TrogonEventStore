using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data.Redaction;
using EventStore.Core.Exceptions;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Synchronization;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Plugins.Transforms;
using Serilog;

namespace EventStore.Core.Services;

public abstract class RedactionService {
	protected static readonly ILogger Log = Serilog.Log.ForContext<RedactionService>();
}

public class RedactionService<TStreamId> :
	RedactionService,
	IAsyncHandle<RedactionMessage.GetEventPosition>,
	IHandle<RedactionMessage.AcquireChunksLock>,
	IAsyncHandle<RedactionMessage.SwitchChunk>,
	IHandle<RedactionMessage.ReleaseChunksLock>,
	IHandle<SystemMessage.BecomeShuttingDown>
{

	private readonly IQueuedHandler _queuedHandler;
	private readonly TFChunkDb _db;
	private readonly IReadIndex<TStreamId> _readIndex;
	private readonly SemaphoreSlimLock _switchChunksLock;

	private const string NewChunkFileExtension = ".tmp";

	public RedactionService(
		IQueuedHandler queuedHandler,
		TFChunkDb db,
		IReadIndex<TStreamId> readIndex,
		SemaphoreSlimLock switchChunksLock)
	{
		Ensure.NotNull(queuedHandler, nameof(queuedHandler));
		Ensure.NotNull(db, nameof(db));
		Ensure.NotNull(readIndex, nameof(readIndex));
		Ensure.NotNull(switchChunksLock, nameof(switchChunksLock));

		_queuedHandler = queuedHandler;
		_db = db;
		_readIndex = readIndex;
		_switchChunksLock = switchChunksLock;
	}

	async ValueTask IAsyncHandle<RedactionMessage.GetEventPosition>.HandleAsync(
		RedactionMessage.GetEventPosition message, CancellationToken token)
	{
		try
		{
			await GetEventPosition(message.EventStreamId, message.EventNumber, message.Envelope, token);
		}
		catch (Exception ex)
		{
			Log.Error(ex,
				"REDACTION: An error has occurred when getting position for stream: {stream}, event number: {eventNumber}.",
				message.EventStreamId, message.EventNumber);
			message.Envelope.ReplyWith(
				new RedactionMessage.GetEventPositionCompleted(GetEventPositionResult.UnexpectedError,
					[]));
		}
	}

	private async ValueTask GetEventPosition(string streamName, long eventNumber, IEnvelope envelope,
		CancellationToken token)
	{
		var streamId = _readIndex.GetStreamId(streamName);
		var result = _readIndex.ReadEventInfo_KeepDuplicates(streamId, eventNumber);

		var eventPositions = new EventPosition[result.EventInfos.Length];

		for (int i = 0; i < result.EventInfos.Length; i++)
		{
			var eventInfo = result.EventInfos[i];
			var logPos = eventInfo.LogPosition;
			var chunk = _db.Manager.GetChunkFor(logPos);
			var localPosition = chunk.ChunkHeader.GetLocalLogPosition(logPos);
			var chunkEventOffset = chunk.GetActualRawPosition(localPosition);

			// all the events returned by ReadEventInfo_KeepDuplicates() must exist in the log
			// since the log record was read from the chunk to check for hash collisions.
			if (chunkEventOffset < 0)
				throw new Exception($"Failed to fetch actual raw position for event at log position: {logPos}");

			if (chunkEventOffset > uint.MaxValue)
				throw new Exception(
					$"Actual raw position for event at log position: {logPos} is larger than uint.MaxValue: {chunkEventOffset}");

			eventPositions[i] = new EventPosition(
				logPosition: logPos,
				chunkFile: Path.GetFileName(chunk.FileName),
				chunkVersion: chunk.ChunkHeader.MinCompatibleVersion,
				chunkComplete: chunk.ChunkFooter is { IsCompleted: true },
				chunkEventOffset: (uint)chunkEventOffset);
		}

		envelope.ReplyWith(
			new RedactionMessage.GetEventPositionCompleted(GetEventPositionResult.Success, eventPositions));
	}

	public void Handle(RedactionMessage.AcquireChunksLock message)
	{
		if (_switchChunksLock.TryAcquire(out var acquisitionId))
		{
			Log.Information("REDACTION: Acquired the chunks lock");
			message.Envelope.ReplyWith(
				new RedactionMessage.AcquireChunksLockCompleted(AcquireChunksLockResult.Success, acquisitionId));
		}
		else
		{
			Log.Information("REDACTION: Failed to acquire the chunks lock");
			message.Envelope.ReplyWith(
				new RedactionMessage.AcquireChunksLockCompleted(AcquireChunksLockResult.Failed, Guid.Empty));
		}
	}

	public void Handle(RedactionMessage.ReleaseChunksLock message)
	{
		if (_switchChunksLock.TryRelease(message.AcquisitionId))
		{
			Log.Information("REDACTION: Released the chunks lock");
			message.Envelope.ReplyWith(
				new RedactionMessage.ReleaseChunksLockCompleted(ReleaseChunksLockResult.Success));
		}
		else
		{
			Log.Information("REDACTION: Failed to release the chunks lock");
			message.Envelope.ReplyWith(
				new RedactionMessage.ReleaseChunksLockCompleted(ReleaseChunksLockResult.Failed));
		}
	}

	async ValueTask IAsyncHandle<RedactionMessage.SwitchChunk>.HandleAsync(RedactionMessage.SwitchChunk message,
		CancellationToken token)
	{
		var currentAcquisitionId = _switchChunksLock.CurrentAcquisitionId;
		if (currentAcquisitionId != message.AcquisitionId)
		{
			Log.Error("REDACTION: Skipping switching of chunk: {targetChunk} with chunk: {newChunk} " +
			          "as the lock is not currently held by the requester. " +
			          "(Requester\'s lock ID: {requestLockId:B}. Current lock ID: {currentLockId:B})",
				message.TargetChunkFile, message.NewChunkFile, message.AcquisitionId, currentAcquisitionId);
			message.Envelope.ReplyWith(
				new RedactionMessage.SwitchChunkCompleted(SwitchChunkResult.UnexpectedError));
			return;
		}

		try
		{
			Log.Information("REDACTION: Replacing chunk {targetChunk} with {newChunk}", message.TargetChunkFile,
				message.NewChunkFile);
			await SwitchChunk(message.TargetChunkFile, message.NewChunkFile, message.Envelope, token);
		}
		catch (Exception ex)
		{
			Log.Error(ex,
				"REDACTION: An error has occurred when trying to switch chunk: {targetChunk} with chunk: {newChunk}.",
				message.TargetChunkFile, message.NewChunkFile);
			message.Envelope.ReplyWith(
				new RedactionMessage.SwitchChunkCompleted(SwitchChunkResult.UnexpectedError));
		}
	}

	private async ValueTask SwitchChunk(string targetChunkFile, string newChunkFile, IEnvelope envelope,
		CancellationToken token)
	{
		Message reply;
		switch (await IsValidSwitchChunkRequest(targetChunkFile, newChunkFile, token))
		{
			case { ValueOrDefault: { } newChunk }:
				await _db.Manager.SwitchChunk(
					chunk: newChunk,
					verifyHash: false,
					removeChunksWithGreaterNumbers: false,
					token);

				reply = new RedactionMessage.SwitchChunkCompleted(SwitchChunkResult.Success);
				break;
			case var result:
				reply = new RedactionMessage.SwitchChunkCompleted(result.Error);
				break;
		}

		envelope.ReplyWith(reply);
	}

	private static bool IsUnsafeFileName(string fileName)
	{
		// protect against directory traversal attacks
		return fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..");
	}

	private async ValueTask<Result<TFChunk, SwitchChunkResult>> IsValidSwitchChunkRequest(string targetChunkFile,
		string newChunkFile, CancellationToken token)
	{
		if (IsUnsafeFileName(targetChunkFile))
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkFileNameInvalid);
		}

		if (IsUnsafeFileName(newChunkFile))
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkFileNameInvalid);
		}

		int targetChunkNumber;
		try
		{
			targetChunkNumber = _db.Config.FileNamingStrategy.GetIndexFor(targetChunkFile);
		}
		catch
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkFileNameInvalid);
		}

		if (Path.GetExtension(newChunkFile) != NewChunkFileExtension)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkFileNameInvalid);
		}

		if (!File.Exists(Path.Combine(_db.Config.Path, targetChunkFile)))
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkFileNotFound);
		}

		var newChunkPath = Path.Combine(_db.Config.Path, newChunkFile);
		if (!File.Exists(newChunkPath))
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkFileNotFound);
		}

		TFChunk targetChunk;
		try
		{
			targetChunk = _db.Manager.GetChunk(targetChunkNumber);
		}
		catch (ArgumentOutOfRangeException)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkExcessive);
		}

		if (Path.GetFileName(targetChunk.FileName) != targetChunkFile)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkInactive);
		}

		if (targetChunk.ChunkFooter is not { IsCompleted: true })
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkNotCompleted);
		}

		if (targetChunk.ChunkHeader.TransformType is not TransformType.Identity)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.TargetChunkFormatNotSupported);
		}

		ChunkHeader newChunkHeader;
		ChunkFooter newChunkFooter;
		try
		{
			await using var fs = new FileStream(newChunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			try
			{
				newChunkHeader = ChunkHeader.FromStream(fs);
				fs.Seek(-ChunkFooter.Size, SeekOrigin.End);
				newChunkFooter = ChunkFooter.FromStream(fs);
			}
			catch
			{
				return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkHeaderOrFooterInvalid);
			}
		}
		catch
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkOpenFailed);
		}

		if (newChunkHeader.ChunkStartNumber != targetChunk.ChunkHeader.ChunkStartNumber ||
		    newChunkHeader.ChunkEndNumber != targetChunk.ChunkHeader.ChunkEndNumber)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.ChunkRangeDoesNotMatch);
		}

		if (!newChunkFooter.IsCompleted)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkNotCompleted);
		}

		try
		{
			// temporarily open the chunk to verify its integrity
			return await TFChunk.FromCompletedFile(
				filename: newChunkPath,
				verifyHash: true,
				unbufferedRead: _db.Config.Unbuffered,
				optimizeReadSideCache: false,
				reduceFileCachePressure: true,
				tracker: new TFChunkTracker.NoOp(),
				getTransformFactory: _db.TransformManager.GetFactoryForExistingChunk,
				token: token);
		}
		catch (HashValidationException)
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkHashInvalid);
		}
		catch
		{
			return new Result<TFChunk, SwitchChunkResult>(SwitchChunkResult.NewChunkOpenFailed);
		}
	}

	public void Handle(SystemMessage.BecomeShuttingDown message)
	{
		// _switchChunksLock is not disposed here since it's shared between multiple services
		_queuedHandler?.RequestStop();
	}
}
