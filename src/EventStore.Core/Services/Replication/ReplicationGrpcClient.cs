using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using Grpc.Core;
using Grpc.Net.Client;
using Serilog.Extensions.Logging;
using Proto = EventStore.Replication;

namespace EventStore.Core.Services.Replication;

public interface IReplicationGrpcClientFactory
{
	IReplicationGrpcClient Create(EndPoint leaderEndPoint);
}

public interface IReplicationGrpcClient : IDisposable
{
	IReplicationGrpcCall Replicate(CancellationToken cancellationToken);
}

public interface IReplicationGrpcCall : IDisposable
{
	Task WriteAsync(Proto.ReplicaFrame frame);
	Task CompleteRequestAsync();
	IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class ReplicationGrpcClientFactory : IReplicationGrpcClientFactory
{
	private static readonly TimeSpan DefaultKeepAlivePingDelay = TimeSpan.FromMilliseconds(700);
	private static readonly TimeSpan DefaultKeepAlivePingTimeout = TimeSpan.FromMilliseconds(700);
	private static readonly TimeSpan MinimumKeepAlivePingValue = TimeSpan.FromSeconds(1);
	private readonly string _uriScheme;
	private readonly INodeHttpClientFactory _nodeHttpClientFactory;
	private readonly TimeSpan _keepAlivePingDelay;
	private readonly TimeSpan _keepAlivePingTimeout;

	public ReplicationGrpcClientFactory(
		string uriScheme,
		INodeHttpClientFactory nodeHttpClientFactory,
		TimeSpan? keepAlivePingDelay = null,
		TimeSpan? keepAlivePingTimeout = null)
	{
		Ensure.NotNullOrEmpty(uriScheme, nameof(uriScheme));
		Ensure.NotNull(nodeHttpClientFactory, nameof(nodeHttpClientFactory));

		_uriScheme = uriScheme;
		_nodeHttpClientFactory = nodeHttpClientFactory;
		_keepAlivePingDelay = NormalizeKeepAliveValue(keepAlivePingDelay ?? DefaultKeepAlivePingDelay);
		_keepAlivePingTimeout = NormalizeKeepAliveValue(keepAlivePingTimeout ?? DefaultKeepAlivePingTimeout);
	}

	private static TimeSpan NormalizeKeepAliveValue(TimeSpan value) =>
		value < MinimumKeepAlivePingValue ? MinimumKeepAlivePingValue : value;

	public IReplicationGrpcClient Create(EndPoint leaderEndPoint)
	{
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));
		return new ReplicationGrpcClient(_uriScheme, leaderEndPoint, _nodeHttpClientFactory,
			_keepAlivePingDelay, _keepAlivePingTimeout);
	}
}

internal sealed class ReplicationGrpcClient : IReplicationGrpcClient
{
	private readonly GrpcChannel _channel;

	public ReplicationGrpcClient(
		string uriScheme,
		EndPoint leaderEndPoint,
		INodeHttpClientFactory nodeHttpClientFactory,
		TimeSpan keepAlivePingDelay,
		TimeSpan keepAlivePingTimeout)
	{
		var httpClient = nodeHttpClientFactory.CreateHttpClient(
			leaderEndPoint.GetOtherNames(),
			handler =>
			{
				handler.KeepAlivePingDelay = keepAlivePingDelay;
				handler.KeepAlivePingTimeout = keepAlivePingTimeout;
				handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always;
			});
		httpClient.Timeout = Timeout.InfiniteTimeSpan;
		httpClient.DefaultRequestVersion = new Version(2, 0);

		var address = new UriBuilder(
			uriScheme,
			leaderEndPoint.GetHost(),
			leaderEndPoint.GetPort()).Uri;
		_channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
		{
			HttpClient = httpClient,
			DisposeHttpClient = true,
			LoggerFactory = new SerilogLoggerFactory()
		});
	}

	public IReplicationGrpcCall Replicate(CancellationToken cancellationToken)
	{
		var client = new Proto.Replication.ReplicationClient(_channel.CreateCallInvoker());
		return new ReplicationGrpcCall(client.Replicate(cancellationToken: cancellationToken));
	}

	public void Dispose() => _channel.Dispose();

	private sealed class ReplicationGrpcCall(
		AsyncDuplexStreamingCall<Proto.ReplicaFrame, Proto.LeaderFrame> call) : IReplicationGrpcCall
	{
		public Task WriteAsync(Proto.ReplicaFrame frame) => call.RequestStream.WriteAsync(frame);

		public Task CompleteRequestAsync() => call.RequestStream.CompleteAsync();

		public async IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			while (await call.ResponseStream.MoveNext(cancellationToken))
			{
				yield return call.ResponseStream.Current;
			}
		}

		public void Dispose() => call.Dispose();
	}
}
