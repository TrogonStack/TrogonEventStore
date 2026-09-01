using System;
using System.Collections.Generic;
using System.Net;
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
	private readonly string _uriScheme;
	private readonly INodeHttpClientFactory _nodeHttpClientFactory;

	public ReplicationGrpcClientFactory(
		string uriScheme,
		INodeHttpClientFactory nodeHttpClientFactory)
	{
		Ensure.NotNullOrEmpty(uriScheme, nameof(uriScheme));
		Ensure.NotNull(nodeHttpClientFactory, nameof(nodeHttpClientFactory));

		_uriScheme = uriScheme;
		_nodeHttpClientFactory = nodeHttpClientFactory;
	}

	public IReplicationGrpcClient Create(EndPoint leaderEndPoint)
	{
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));
		return new ReplicationGrpcClient(_uriScheme, leaderEndPoint, _nodeHttpClientFactory);
	}
}

internal sealed class ReplicationGrpcClient : IReplicationGrpcClient
{
	private readonly GrpcChannel _channel;

	public ReplicationGrpcClient(
		string uriScheme,
		EndPoint leaderEndPoint,
		INodeHttpClientFactory nodeHttpClientFactory)
	{
		var httpClient = nodeHttpClientFactory.CreateHttpClient(leaderEndPoint.GetOtherNames());
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
