using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Transport.Grpc.Replication;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Proto = EventStore.Replication;

namespace EventStore.Core.Tests.Services.Replication.LogReplication;

internal sealed class InProcessReplicationGrpcClient(ReplicationService service) : IReplicationGrpcClient
{
	public IReplicationGrpcCall Replicate(CancellationToken cancellationToken) =>
		new Call(service, cancellationToken);

	public void Dispose()
	{
	}

	private sealed class Call : IReplicationGrpcCall
	{
		private readonly CancellationTokenSource _cancellation;
		private readonly Channel<Proto.ReplicaFrame> _requests;
		private readonly Channel<Proto.LeaderFrame> _responses;
		private readonly Task _serverTask;

		public Call(ReplicationService service, CancellationToken cancellationToken)
		{
			_cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_requests = System.Threading.Channels.Channel.CreateUnbounded<Proto.ReplicaFrame>(new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = true
			});
			_responses = System.Threading.Channels.Channel.CreateUnbounded<Proto.LeaderFrame>(new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = true
			});

			_serverTask = RunServerAsync(service);
		}

		public Task WriteAsync(Proto.ReplicaFrame frame) =>
			_requests.Writer.WriteAsync(frame, _cancellation.Token).AsTask();

		public Task CompleteRequestAsync()
		{
			_requests.Writer.TryComplete();
			return Task.CompletedTask;
		}

		public async IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(
				_cancellation.Token, cancellationToken);
			await foreach (var response in _responses.Reader.ReadAllAsync(linked.Token))
			{
				yield return response;
			}
		}

		public void Dispose()
		{
			_requests.Writer.TryComplete();
			_cancellation.Cancel();
		}

		private async Task RunServerAsync(ReplicationService service)
		{
			try
			{
				await service.Replicate(
					new ChannelStreamReader<Proto.ReplicaFrame>(_requests.Reader),
					new ChannelStreamWriter<Proto.LeaderFrame>(_responses.Writer, _cancellation.Token),
					new TestServerCallContext(_cancellation.Token));
				_responses.Writer.TryComplete();
			}
			catch (Exception exception)
			{
				_responses.Writer.TryComplete(exception);
			}
		}
	}

	private sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
	{
		public T Current { get; private set; }

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			while (await reader.WaitToReadAsync(cancellationToken))
			{
				if (reader.TryRead(out var value))
				{
					Current = value;
					return true;
				}
			}

			return false;
		}
	}

	private sealed class ChannelStreamWriter<T>(ChannelWriter<T> writer, CancellationToken cancellationToken)
		: IServerStreamWriter<T>
	{
		public WriteOptions WriteOptions { get; set; }

		public Task WriteAsync(T message) => writer.WriteAsync(message, cancellationToken).AsTask();
	}

	private sealed class TestServerCallContext : ServerCallContext
	{
		private readonly CancellationToken _cancellationToken;

		public TestServerCallContext(CancellationToken cancellationToken)
		{
			_cancellationToken = cancellationToken;
			UserStateCore["__HttpContext"] = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity())
			};
		}

		protected override string MethodCore => "/event_store.replication.Replication/Replicate";
		protected override string HostCore => "localhost";
		protected override string PeerCore => "ipv4:127.0.0.1:2113";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => _cancellationToken;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions WriteOptionsCore { get; set; }
		protected override AuthContext AuthContextCore { get; } =
			new(string.Empty, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
			throw new NotSupportedException();
	}
}

internal sealed class InProcessGrpcReplicaServiceFactory(
	IReplicationGrpcClient client,
	IReplicaSubscriptionDataSource dataSource,
	Guid replicaInstanceId) : IGrpcReplicaServiceFactory
{
	public IGrpcReplicaService Create(IPublisher publisher, GrpcReplicaConnectionEndpoints endpoints) =>
		new GrpcReplicaService(
			publisher,
			client,
			dataSource,
			replicaInstanceId,
			endpoints.LeaderEndPoint,
			endpoints.AdvertisedReplicaEndPoint,
			ReplicaPromotability.Promotable,
			requestQueueCapacity: 2);
}
