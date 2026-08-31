using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using Grpc.Core;
using Grpc.Net.Client;
using Serilog.Extensions.Logging;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Services.RequestForwarding;

public interface IRequestForwardingGrpcClientFactory
{
	ForwardingTransportSecurity TransportSecurity { get; }
	IRequestForwardingGrpcClient Create(EndPoint leaderEndPoint);
}

public interface IRequestForwardingGrpcClient : IDisposable
{
	IRequestForwardingGrpcCall Forward(CancellationToken cancellationToken);
}

public interface IRequestForwardingGrpcCall : IDisposable
{
	Task WriteAsync(Proto.FollowerFrame frame);
	Task CompleteRequestAsync();
	IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class RequestForwardingGrpcClientFactory : IRequestForwardingGrpcClientFactory
{
	private static readonly TimeSpan DefaultKeepAlivePingDelay = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan DefaultKeepAlivePingTimeout = TimeSpan.FromSeconds(10);
	private readonly string _uriScheme;
	private readonly INodeHttpClientFactory _nodeHttpClientFactory;
	private readonly TimeSpan _keepAlivePingDelay;
	private readonly TimeSpan _keepAlivePingTimeout;

	public RequestForwardingGrpcClientFactory(
		string uriScheme,
		INodeHttpClientFactory nodeHttpClientFactory,
		TimeSpan? keepAlivePingDelay = null,
		TimeSpan? keepAlivePingTimeout = null)
	{
		Ensure.NotNullOrEmpty(uriScheme, nameof(uriScheme));
		Ensure.NotNull(nodeHttpClientFactory, nameof(nodeHttpClientFactory));

		_uriScheme = uriScheme;
		TransportSecurity = string.Equals(uriScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
			? ForwardingTransportSecurity.Tls
			: string.Equals(uriScheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
				? ForwardingTransportSecurity.Cleartext
				: throw new ArgumentOutOfRangeException(nameof(uriScheme), uriScheme,
					"Request forwarding supports only HTTP and HTTPS URI schemes.");
		_nodeHttpClientFactory = nodeHttpClientFactory;
		_keepAlivePingDelay = keepAlivePingDelay ?? DefaultKeepAlivePingDelay;
		_keepAlivePingTimeout = keepAlivePingTimeout ?? DefaultKeepAlivePingTimeout;
	}

	public ForwardingTransportSecurity TransportSecurity { get; }

	public IRequestForwardingGrpcClient Create(EndPoint leaderEndPoint)
	{
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));
		return new RequestForwardingGrpcClient(
			_uriScheme,
			leaderEndPoint,
			_nodeHttpClientFactory,
			_keepAlivePingDelay,
			_keepAlivePingTimeout);
	}
}

internal sealed class RequestForwardingGrpcClient : IRequestForwardingGrpcClient
{
	private readonly GrpcChannel _channel;

	public RequestForwardingGrpcClient(
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

	public IRequestForwardingGrpcCall Forward(CancellationToken cancellationToken)
	{
		var client = new Proto.RequestForwarding.RequestForwardingClient(_channel.CreateCallInvoker());
		return new RequestForwardingGrpcCall(client.Forward(cancellationToken: cancellationToken));
	}

	public void Dispose() => _channel.Dispose();

	private sealed class RequestForwardingGrpcCall(
		AsyncDuplexStreamingCall<Proto.FollowerFrame, Proto.LeaderFrame> call) : IRequestForwardingGrpcCall
	{
		public Task WriteAsync(Proto.FollowerFrame frame) => call.RequestStream.WriteAsync(frame);

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
