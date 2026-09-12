using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace EventStore.ClusterNode.Components.Services;

public sealed class NodeConnectionTracker
{
	private readonly ConcurrentDictionary<string, NodeConnectionState> _connections = new();

	public IReadOnlyList<NodeConnectionSnapshot> Snapshot() =>
		_connections.Values.Select(x => x.Snapshot())
			.OrderBy(x => x.RemoteEndPoint, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.ConnectionId, StringComparer.Ordinal)
			.ToArray();

	public async Task Track(ConnectionContext context, ConnectionDelegate next, bool isTls)
	{
		var state = new NodeConnectionState(
			context.ConnectionId,
			context.RemoteEndPoint?.ToString() ?? "",
			context.LocalEndPoint?.ToString() ?? "",
			isTls,
			DateTimeOffset.UtcNow);
		_connections[context.ConnectionId] = state;
		context.Transport = new CountingDuplexPipe(context.Transport, state);

		try
		{
			await next(context);
		}
		finally
		{
			_connections.TryRemove(context.ConnectionId, out _);
		}
	}

	public void ObserveRequest(
		string connectionId,
		string protocol,
		bool isGrpc,
		string connectionName,
		string userAgent)
	{
		if (_connections.TryGetValue(connectionId, out var connection))
			connection.ObserveRequest(protocol, isGrpc, connectionName, userAgent);
	}
}

public sealed record NodeConnectionSnapshot(
	string ConnectionId,
	string RemoteEndPoint,
	string LocalEndPoint,
	string ClientName,
	string Application,
	string Protocol,
	bool IsTls,
	DateTimeOffset ConnectedAt,
	long TotalBytesSent,
	long TotalBytesReceived,
	long PendingSendBytes,
	long PendingReceivedBytes);

internal sealed class NodeConnectionState
{
	private readonly object _metadataLock = new();
	private string _clientName = "";
	private bool _hasExplicitConnectionName;
	private bool _hasGrpcRequests;
	private bool _hasHttpRequests;
	private long _pendingReceivedBytes;
	private long _pendingSendBytes;
	private string _protocol = "";
	private long _totalBytesReceived;
	private long _totalBytesSent;

	public NodeConnectionState(
		string connectionId,
		string remoteEndPoint,
		string localEndPoint,
		bool isTls,
		DateTimeOffset connectedAt)
	{
		ConnectionId = connectionId;
		RemoteEndPoint = remoteEndPoint;
		LocalEndPoint = localEndPoint;
		IsTls = isTls;
		ConnectedAt = connectedAt;
	}

	private string ConnectionId { get; }
	private string RemoteEndPoint { get; }
	private string LocalEndPoint { get; }
	private bool IsTls { get; }
	private DateTimeOffset ConnectedAt { get; }

	public void Received(long bytes, long pendingBytes)
	{
		Interlocked.Add(ref _totalBytesReceived, bytes);
		Interlocked.Exchange(ref _pendingReceivedBytes, pendingBytes);
	}

	public void Reading(long pendingBytes) =>
		Interlocked.Exchange(ref _pendingReceivedBytes, pendingBytes);

	public void Sending(int bytes)
	{
		Interlocked.Add(ref _totalBytesSent, bytes);
		Interlocked.Add(ref _pendingSendBytes, bytes);
	}

	public void Sent() => Interlocked.Exchange(ref _pendingSendBytes, 0);

	public void ObserveRequest(
		string protocol,
		bool isGrpc,
		string connectionName,
		string userAgent)
	{
		lock (_metadataLock)
		{
			_protocol = Merge(_protocol, protocol);
			_hasGrpcRequests |= isGrpc;
			_hasHttpRequests |= !isGrpc;

			if (!string.IsNullOrWhiteSpace(connectionName))
			{
				_clientName = connectionName;
				_hasExplicitConnectionName = true;
			}
			else if (!_hasExplicitConnectionName && !string.IsNullOrWhiteSpace(userAgent))
			{
				_clientName = userAgent;
			}
		}
	}

	public NodeConnectionSnapshot Snapshot()
	{
		lock (_metadataLock)
		{
			return new(
				ConnectionId,
				RemoteEndPoint,
				LocalEndPoint,
				_clientName,
				ApplicationLabel(),
				_protocol,
				IsTls,
				ConnectedAt,
				Interlocked.Read(ref _totalBytesSent),
				Interlocked.Read(ref _totalBytesReceived),
				Interlocked.Read(ref _pendingSendBytes),
				Interlocked.Read(ref _pendingReceivedBytes));
		}
	}

	private string ApplicationLabel() => (_hasHttpRequests, _hasGrpcRequests) switch
	{
		(true, true) => "HTTP and gRPC",
		(false, true) => "gRPC",
		(true, false) => "HTTP",
		_ => "Awaiting request"
	};

	private static string Merge(string current, string observed)
	{
		if (string.IsNullOrWhiteSpace(observed) || current == observed)
			return current;
		return string.IsNullOrWhiteSpace(current) ? observed : "Mixed";
	}
}

internal sealed class CountingDuplexPipe : IDuplexPipe
{
	public CountingDuplexPipe(IDuplexPipe inner, NodeConnectionState state)
	{
		Input = new CountingPipeReader(inner.Input, state);
		Output = new CountingPipeWriter(inner.Output, state);
	}

	public PipeReader Input { get; }
	public PipeWriter Output { get; }
}

internal sealed class CountingPipeReader : PipeReader
{
	private readonly PipeReader _inner;
	private readonly NodeConnectionState _state;
	private ReadOnlySequence<byte> _currentBuffer;

	public CountingPipeReader(PipeReader inner, NodeConnectionState state)
	{
		_inner = inner;
		_state = state;
	}

	public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

	public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
	{
		var consumedBytes = _currentBuffer.IsEmpty ? 0 : _currentBuffer.Slice(0, consumed).Length;
		var pendingBytes = _currentBuffer.IsEmpty ? 0 : _currentBuffer.Slice(consumed).Length;
		_state.Received(consumedBytes, pendingBytes);
		_currentBuffer = default;
		_inner.AdvanceTo(consumed, examined);
	}

	public override void CancelPendingRead() => _inner.CancelPendingRead();

	public override void Complete(Exception exception = null) => _inner.Complete(exception);

	public override ValueTask CompleteAsync(Exception exception = null) => _inner.CompleteAsync(exception);

	public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
	{
		var result = await _inner.ReadAsync(cancellationToken);
		Observe(result);
		return result;
	}

	public override bool TryRead(out ReadResult result)
	{
		if (!_inner.TryRead(out result))
			return false;

		Observe(result);
		return true;
	}

	private void Observe(ReadResult result)
	{
		_currentBuffer = result.Buffer;
		_state.Reading(result.Buffer.Length);
	}
}

internal sealed class CountingPipeWriter : PipeWriter
{
	private readonly PipeWriter _inner;
	private readonly NodeConnectionState _state;

	public CountingPipeWriter(PipeWriter inner, NodeConnectionState state)
	{
		_inner = inner;
		_state = state;
	}

	public override void Advance(int bytes)
	{
		_state.Sending(bytes);
		_inner.Advance(bytes);
	}

	public override void CancelPendingFlush() => _inner.CancelPendingFlush();

	public override void Complete(Exception exception = null) => _inner.Complete(exception);

	public override ValueTask CompleteAsync(Exception exception = null) => _inner.CompleteAsync(exception);

	public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
	{
		var result = await _inner.FlushAsync(cancellationToken);
		if (!result.IsCanceled)
			_state.Sent();
		return result;
	}

	public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

	public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
}
