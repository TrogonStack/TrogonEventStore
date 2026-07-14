using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Archive.Archiver.Unmerger;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using Serilog;

namespace EventStore.Core.Services.Archive.Archiver;

public class ArchiverService :
	IHandle<SystemMessage.ChunkLoaded>,
	IHandle<SystemMessage.ChunkCompleted>,
	IHandle<SystemMessage.ChunkSwitched>,
	IHandle<ReplicationTrackingMessage.ReplicatedTo>,
	IHandle<SystemMessage.BecomeShuttingDown>
{
	private static readonly ILogger Log = Serilog.Log.ForContext<ArchiverService>();

	private readonly ISubscriber _mainBus;
	private readonly IArchiveStorageWriter _archiveWriter;
	private readonly IArchiveStorageReader _archiveReader;
	private readonly Queue<ChunkInfo> _uncommittedChunks;
	private readonly SortedDictionary<(int StartNumber, int EndNumber, string Locator), ChunkInfo> _chunksToArchive;
	private readonly object _chunksToArchiveLock = new();
	private readonly CancellationTokenSource _cts;
	private readonly Channel<bool> _archiveSignal;
	private readonly IChunkUnmerger _chunkUnmerger;
	private readonly IArchiveChunkNamer _chunkNamer;

	private readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
	private long _replicationPosition;
	private bool _archivingStarted;
	private long _checkpoint;

	public ArchiverService(
		ISubscriber mainBus,
		IArchiveStorageFactory archiveStorageFactory,
		IChunkUnmerger chunkUnmerger,
		IArchiveChunkNamer chunkNamer)
	{
		_mainBus = mainBus;
		_archiveWriter = archiveStorageFactory.CreateWriter();
		_archiveReader = archiveStorageFactory.CreateReader();
		_chunkUnmerger = chunkUnmerger;
		_chunkNamer = chunkNamer;

		_uncommittedChunks = new();
		_chunksToArchive = new();
		_cts = new();
		_archiveSignal = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
		{
			SingleWriter = false,
			SingleReader = true
		});

		Subscribe();
	}

	private void Subscribe()
	{
		_mainBus.Subscribe<SystemMessage.ChunkLoaded>(this);
		_mainBus.Subscribe<SystemMessage.ChunkSwitched>(this);
		_mainBus.Subscribe<SystemMessage.ChunkCompleted>(this);
		_mainBus.Subscribe<ReplicationTrackingMessage.ReplicatedTo>(this);
		_mainBus.Subscribe<SystemMessage.BecomeShuttingDown>(this);
	}

	public void Handle(SystemMessage.ChunkLoaded message)
	{
		if (!message.ChunkInfo.IsCompleted || message.ChunkInfo.IsRemote)
		{
			return;
		}

		ScheduleChunkForArchiving(message.ChunkInfo, "old");
	}

	public void Handle(SystemMessage.ChunkCompleted message)
	{
		var chunkInfo = message.ChunkInfo;
		if (chunkInfo.IsRemote)
		{
			return;
		}

		if (chunkInfo.ChunkEndPosition > _replicationPosition)
		{
			_uncommittedChunks.Enqueue(chunkInfo);
			return;
		}

		ScheduleChunkForArchiving(chunkInfo, "new");
	}

	public void Handle(SystemMessage.ChunkSwitched message)
	{
		if (message.ChunkInfo.IsRemote)
		{
			return;
		}

		ScheduleChunkForArchiving(message.ChunkInfo, "changed");
	}

	public void Handle(ReplicationTrackingMessage.ReplicatedTo message)
	{
		_replicationPosition = Math.Max(_replicationPosition, message.LogPosition);
		ProcessUncommittedChunks();

		if (_archivingStarted)
		{
			return;
		}

		_archivingStarted = true;
		Task.Run(() => StartArchiving(_cts.Token), _cts.Token);
	}

	private async Task StartArchiving(CancellationToken ct)
	{
		try
		{
			await LoadArchiveCheckpoint(ct);
			ScheduleExistingChunksForArchiving();
			await ArchiveChunks(ct);
		}
		catch (OperationCanceledException)
		{
			// ignore
		}
		catch (Exception ex)
		{
			Log.Fatal(ex, "Archiving has stopped working due to an unhandled exception.");
		}
	}

	public void Handle(SystemMessage.BecomeShuttingDown message)
	{
		try
		{
			_cts.Cancel();
		}
		catch
		{
			// ignore
		}
		finally
		{
			_cts?.Dispose();
		}
	}

	private void ProcessUncommittedChunks()
	{
		while (_uncommittedChunks.TryPeek(out var chunkInfo))
		{
			if (chunkInfo.ChunkEndPosition > _replicationPosition)
			{
				break;
			}

			_uncommittedChunks.Dequeue();
			ScheduleChunkForArchiving(chunkInfo, "new");
		}
	}

	private void ScheduleChunkForArchiving(ChunkInfo chunkInfo, string chunkType)
	{
		lock (_chunksToArchiveLock)
		{
			if (chunkInfo.ChunkEndPosition <= _checkpoint)
			{
				return;
			}

			_chunksToArchive[(chunkInfo.ChunkStartNumber, chunkInfo.ChunkEndNumber, chunkInfo.ChunkLocator)] = chunkInfo;
		}

		_archiveSignal.Writer.TryWrite(true);

		Log.Information("Scheduled archiving of {chunkFile} ({chunkType})",
			Path.GetFileName(chunkInfo.ChunkLocator), chunkType);
	}

	private async Task ArchiveChunks(CancellationToken ct)
	{
		while (await _archiveSignal.Reader.WaitToReadAsync(ct))
		{
			while (_archiveSignal.Reader.TryRead(out _))
			{
			}

			while (TryGetNextChunkToArchive(out var chunkInfo))
			{
				await ArchiveChunk(chunkInfo, ct);
			}
		}
	}

	private bool TryGetNextChunkToArchive(out ChunkInfo chunkInfo)
	{
		lock (_chunksToArchiveLock)
		{
			while (_chunksToArchive.Count > 0)
			{
				var (key, candidate) = _chunksToArchive.First();
				if (candidate.ChunkEndPosition <= _checkpoint)
				{
					_chunksToArchive.Remove(key);
					continue;
				}

				if (candidate.ChunkStartPosition > _checkpoint)
				{
					break;
				}

				_chunksToArchive.Remove(key);
				chunkInfo = candidate;
				return true;
			}
		}

		chunkInfo = default;
		return false;
	}

	private async Task ArchiveChunk(ChunkInfo chunkInfo, CancellationToken ct)
	{
		var chunkPath = chunkInfo.ChunkLocator;
		var chunkFile = Path.GetFileName(chunkPath);
		try
		{
			Log.Information("Archiving {chunkFile}", chunkFile);

			string[] chunksToStore;
			bool chunksUnmerged;

			if (chunkInfo.ChunkStartNumber == chunkInfo.ChunkEndNumber)
			{
				chunksToStore = [chunkPath];
				chunksUnmerged = false;
			}
			else
			{
				Log.Information("Unmerging {chunkFile}", chunkFile);
				chunksToStore = await _chunkUnmerger.Unmerge(
						chunkPath,
						chunkInfo.ChunkStartNumber,
						chunkInfo.ChunkEndNumber)
					.ToArrayAsync(cancellationToken: ct);
				chunksUnmerged = true;
			}

			var logicalChunkNumber = chunkInfo.ChunkStartNumber;
			foreach (var chunkToStore in chunksToStore)
			{
				var destinationFile = _chunkNamer.GetFileNameFor(logicalChunkNumber);

				while (!await _archiveWriter.StoreChunk(chunkToStore, destinationFile, ct))
				{
					Log.Warning("Archiving of {chunkFile}{chunkDetails} failed. Retrying in: {retryInterval}.",
						Path.GetFileName(chunkPath),
						chunksUnmerged ? $" (logical chunk no.: {logicalChunkNumber})" : string.Empty,
						RetryInterval);
					await Task.Delay(RetryInterval, ct);
				}

				if (chunksUnmerged)
				{
					File.Delete(chunkToStore);
				}

				logicalChunkNumber++;
			}

			if (chunkInfo.ChunkEndPosition > _checkpoint)
			{
				while (!await _archiveWriter.SetCheckpoint(chunkInfo.ChunkEndPosition, ct))
				{
					Log.Warning(
						"Failed to set the archive checkpoint to: 0x{checkpoint:X}. Retrying in: {retryInterval}.",
						chunkInfo.ChunkEndPosition, RetryInterval);
					await Task.Delay(RetryInterval, ct);
				}

				lock (_chunksToArchiveLock)
				{
					_checkpoint = chunkInfo.ChunkEndPosition;
				}

				Log.Debug("Archive checkpoint set to: 0x{checkpoint:X}", chunkInfo.ChunkEndPosition);
			}

			Log.Information("Archiving of {chunkFile} succeeded.", chunkFile);
		}
		catch (ChunkDeletedException)
		{
			// the chunk has been deleted, presumably during scavenge or redaction
			Log.Information("Archiving of {chunkFile} cancelled as it was deleted.", Path.GetFileName(chunkPath));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Archiving of {chunkFile} failed.", chunkFile);
			throw;
		}
	}

	private async Task LoadArchiveCheckpoint(CancellationToken ct)
	{
		do
		{
			try
			{
				_checkpoint = await _archiveReader.GetCheckpoint(ct);
				Log.Debug("Archive checkpoint is: 0x{checkpoint:X}", _checkpoint);
				return;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Log.Warning(ex, "Failed to load the archive checkpoint. Retrying in: {retryInterval}.", RetryInterval);
				await Task.Delay(RetryInterval, ct);
			}
		} while (true);
	}

	private void ScheduleExistingChunksForArchiving()
	{
		var scheduledChunks = 0;
		lock (_chunksToArchiveLock)
		{
			foreach (var (key, chunkInfo) in _chunksToArchive.ToArray())
			{
				if (chunkInfo.ChunkEndPosition > _checkpoint)
				{
					scheduledChunks++;
					continue;
				}

				_chunksToArchive.Remove(key);
			}
		}

		_archiveSignal.Writer.TryWrite(true);
		Log.Information("Scheduled archiving of {numChunks} existing chunks.", scheduledChunks);
	}
}
