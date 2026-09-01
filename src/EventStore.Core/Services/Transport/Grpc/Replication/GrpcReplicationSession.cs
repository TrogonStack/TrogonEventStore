#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.TransactionLog.Chunks;
using Google.Protobuf;
using Grpc.Core;
using Proto = EventStore.Replication;

namespace EventStore.Core.Services.Transport.Grpc.Replication;

public sealed class GrpcReplicationSession : IReplicationSession, IAsyncDisposable
{
	private readonly IServerStreamWriter<Proto.LeaderFrame> _responseStream;
	private readonly Channel<QueuedFrame> _outbound;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly CancellationTokenRegistration _callCancellationRegistration;
	private readonly object _positionLock = new();
	private readonly object _sendLock = new();
	private readonly int _maxPendingSends;
	private RpcException? _terminalFailure;
	private long _activeChunkStartPosition;
	private long _activeChunkEndPosition;
	private int _activeChunkStartNumber;
	private int _activeChunkEndNumber;
	private bool _hasActiveChunk;
	private int _isClosed;
	private int _sendQueueSize;
	private int _pendingSendBytes;
	private long _totalBytesSent;
	private long _totalBytesReceived;
	private long _sentReplicationPosition = -1;

	public GrpcReplicationSession(
		ReplicationSessionIdentity identity,
		Guid connectionId,
		IServerStreamWriter<Proto.LeaderFrame> responseStream,
		int capacity,
		CancellationToken callCancellation)
	{
		if (connectionId == Guid.Empty)
		{
			throw new ArgumentException("The connection ID must not be empty.", nameof(connectionId));
		}

		ArgumentNullException.ThrowIfNull(responseStream);
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

		Identity = identity;
		ConnectionId = connectionId;
		_responseStream = responseStream;
		_maxPendingSends = checked(capacity + 1);
		_outbound = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(_maxPendingSends)
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false,
			FullMode = BoundedChannelFullMode.Wait
		});
		_callCancellationRegistration = callCancellation.UnsafeRegister(
			static state => ((GrpcReplicationSession)state!).Close("Replication call cancelled."), this);
		Completion = WriteResponses();
	}

	public ReplicationSessionIdentity Identity { get; }
	public Guid ConnectionId { get; }
	public long SentReplicationPosition => Interlocked.Read(ref _sentReplicationPosition);
	public int SendQueueSize => Volatile.Read(ref _sendQueueSize);
	public bool IsClosed => Volatile.Read(ref _isClosed) != 0;
	public Task Completion { get; }
	internal CancellationToken CancellationToken => _lifetime.Token;
	internal RpcException? TerminalFailure => Volatile.Read(ref _terminalFailure);

	public ReplicationSessionStatistics GetStatistics() => new(
		SendQueueSize,
		Interlocked.Read(ref _totalBytesSent),
		Interlocked.Read(ref _totalBytesReceived),
		Volatile.Read(ref _pendingSendBytes),
		0);

	public ReplicationSendResult TrySend(Message message)
	{
		ArgumentNullException.ThrowIfNull(message);
		lock (_sendLock)
		{
			if (IsClosed)
			{
				return ReplicationSendResult.Closed;
			}
			if (SendQueueSize >= _maxPendingSends)
			{
				return ReplicationSendResult.QueueFull;
			}

			Enqueue(message);
			return ReplicationSendResult.Sent;
		}
	}

	public ReplicationSendResult TrySend(IReadOnlyList<Message> messages)
	{
		ArgumentNullException.ThrowIfNull(messages);
		foreach (var message in messages)
		{
			ArgumentNullException.ThrowIfNull(message);
		}

		lock (_sendLock)
		{
			if (IsClosed)
			{
				return ReplicationSendResult.Closed;
			}
			if (messages.Count > _maxPendingSends - SendQueueSize)
			{
				return ReplicationSendResult.QueueFull;
			}

			foreach (var message in messages)
			{
				Enqueue(message);
			}
			return ReplicationSendResult.Sent;
		}
	}

	public void Reject(ReplicationSessionRejection rejection) =>
		Terminate(new RpcException(new Status(StatusCode.FailedPrecondition, rejection.Reason)));

	public void Close(string reason)
	{
		lock (_sendLock)
		{
			if (Interlocked.CompareExchange(ref _isClosed, 1, 0) != 0)
			{
				return;
			}

			_outbound.Writer.TryComplete();
		}

		_lifetime.Cancel();
	}

	internal void RecordReceived(IMessage frame) =>
		Interlocked.Add(ref _totalBytesReceived, frame.CalculateSize());

	public async ValueTask DisposeAsync()
	{
		Close("Replication session disposed.");
		try
		{
			await Completion.ConfigureAwait(false);
		}
		catch (RpcException)
		{
		}
		_callCancellationRegistration.Dispose();
		_lifetime.Dispose();
	}

	private async Task WriteResponses()
	{
		try
		{
			await foreach (var queued in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
			{
				try
				{
					await _responseStream.WriteAsync(queued.Frame).ConfigureAwait(false);
					Interlocked.Add(ref _totalBytesSent, queued.Size);
					if (queued.ReplicationPosition is { } replicationPosition)
					{
						Interlocked.Exchange(ref _sentReplicationPosition, replicationPosition);
					}
				}
				finally
				{
					Interlocked.Decrement(ref _sendQueueSize);
					Interlocked.Add(ref _pendingSendBytes, -queued.Size);
				}
			}
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			Terminate(exception as RpcException ?? new RpcException(
				new Status(StatusCode.Unavailable, "The replication response stream failed."), exception.Message));
		}
		finally
		{
			while (_outbound.Reader.TryRead(out var queued))
			{
				Interlocked.Decrement(ref _sendQueueSize);
				Interlocked.Add(ref _pendingSendBytes, -queued.Size);
			}
		}

		if (TerminalFailure is { } terminalFailure)
		{
			throw terminalFailure;
		}
	}

	private void Terminate(RpcException failure)
	{
		lock (_sendLock)
		{
			if (Interlocked.CompareExchange(ref _isClosed, 1, 0) != 0)
			{
				return;
			}

			Volatile.Write(ref _terminalFailure, failure);
			_outbound.Writer.TryComplete();
		}

		_lifetime.Cancel();
	}

	private void Enqueue(Message message)
	{
		var frame = ToGrpc(message);
		var replicationPosition = GetReplicationPosition(message);
		var size = frame.CalculateSize();
		Interlocked.Increment(ref _sendQueueSize);
		Interlocked.Add(ref _pendingSendBytes, size);
		if (!_outbound.Writer.TryWrite(new QueuedFrame(frame, size, replicationPosition)))
		{
			Interlocked.Decrement(ref _sendQueueSize);
			Interlocked.Add(ref _pendingSendBytes, -size);
			throw new InvalidOperationException("The replication response queue rejected an admitted frame.");
		}
	}

	private static Proto.LeaderFrame ToGrpc(Message message) => message switch
	{
		ReplicationMessage.ReplicaSubscriptionRetry value => ReplicationGrpcCodec.ToGrpc(value),
		ReplicationMessage.ReplicaSubscribed value => ReplicationGrpcCodec.ToGrpc(value),
		ReplicationMessage.CreateChunk value => ReplicationGrpcCodec.ToGrpc(value),
		// Leader replication transfers dedicated payload arrays that remain immutable after Send.
		ReplicationMessage.RawChunkBulk value => ReplicationGrpcCodec.ToGrpcOwned(value),
		ReplicationMessage.DataChunkBulk value => ReplicationGrpcCodec.ToGrpcOwned(value),
		ReplicationMessage.FollowerAssignment value => ReplicationGrpcCodec.ToGrpc(value),
		ReplicationMessage.CloneAssignment value => ReplicationGrpcCodec.ToGrpc(value),
		ReplicationMessage.DropSubscription value => ReplicationGrpcCodec.ToGrpc(value),
		ReplicationTrackingMessage.ReplicatedTo value => ReplicationGrpcCodec.ToGrpc(value),
		_ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType(),
			"Unsupported leader replication message.")
	};

	private long? GetReplicationPosition(Message message)
	{
		lock (_positionLock)
		{
			switch (message)
			{
				case ReplicationMessage.ReplicaSubscribed subscribed:
					return subscribed.SubscriptionPosition;
				case ReplicationMessage.CreateChunk createChunk:
					_activeChunkStartPosition = createChunk.ChunkHeader.ChunkStartPosition;
					_activeChunkEndPosition = createChunk.ChunkHeader.ChunkEndPosition;
					_activeChunkStartNumber = createChunk.ChunkHeader.ChunkStartNumber;
					_activeChunkEndNumber = createChunk.ChunkHeader.ChunkEndNumber;
					_hasActiveChunk = true;
					return _activeChunkStartPosition;
				case ReplicationMessage.RawChunkBulk raw:
					if (!_hasActiveChunk ||
						raw.ChunkStartNumber != _activeChunkStartNumber ||
						raw.ChunkEndNumber != _activeChunkEndNumber)
					{
						return null;
					}

					var rawPosition = raw.CompleteChunk
						? _activeChunkEndPosition
						: _activeChunkStartPosition + raw.RawPosition - ChunkHeader.Size + raw.RawBytes.Length;
					if (raw.CompleteChunk)
					{
						_hasActiveChunk = false;
					}

					return rawPosition;
				case ReplicationMessage.DataChunkBulk data:
					var matchesActiveChunk = _hasActiveChunk &&
						data.ChunkStartNumber == _activeChunkStartNumber &&
						data.ChunkEndNumber == _activeChunkEndNumber;
					var dataPosition = data.SubscriptionPosition + data.DataBytes.Length;
					if (data.CompleteChunk)
					{
						dataPosition = data.ChunkEndPosition ??
							(matchesActiveChunk ? _activeChunkEndPosition : dataPosition);
					}
					if (data.CompleteChunk && matchesActiveChunk)
					{
						_hasActiveChunk = false;
					}

					return dataPosition;
				default:
					return null;
			}
		}
	}

	private readonly record struct QueuedFrame(Proto.LeaderFrame Frame, int Size, long? ReplicationPosition);
}
