using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.MonitoringTests;

[TestFixture]
public class MonitoringAuthorizationKestrelTests
{
	[Test]
	public async Task authorized_caller_can_read_each_monitoring_endpoint()
	{
		var publisher = new ServingPublisher();
		var connections = new CountingConnections();
		var authorization = new CapturingAuthorizationProvider(true);
		using var host = CreateHost(publisher, connections, authorization);
		await host.StartAsync();
		var address = host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		using var handler = new SocketsHttpHandler();
		using var httpClient = new HttpClient(handler);
		using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = httpClient });
		var client = new EventStore.Client.Monitoring.Monitoring.MonitoringClient(channel);

		var connectionResponse = await client.ConnectionStatsAsync(new ConnectionStatsReq());
		var replicationResponse = await client.ReplicationStatsAsync(new ReplicationStatsReq());
		using var statsCall = client.Stats(new StatsReq { RefreshTimePeriodInMs = 1000 });
		var hasStats = await statsCall.ResponseStream.MoveNext();

		Assert.Multiple(() =>
		{
			Assert.That(connectionResponse, Is.Not.Null);
			Assert.That(replicationResponse, Is.Not.Null);
			Assert.That(hasStats, Is.True);
			Assert.That(connections.SnapshotCalls, Is.EqualTo(1));
			Assert.That(publisher.PublishCalls, Is.EqualTo(2));
			Assert.That(authorization.Operations, Is.EquivalentTo(new[] {
				new Operation(Operations.Node.Statistics.Read),
				new Operation(Operations.Node.Statistics.Replication),
				new Operation(Operations.Node.Statistics.Read)
			}));
		});
	}

	[Test]
	public async Task denied_caller_cannot_read_any_monitoring_data()
	{
		var publisher = new CountingPublisher();
		var connections = new CountingConnections();
		var authorization = new CapturingAuthorizationProvider(false);
		using var host = CreateHost(publisher, connections, authorization);
		await host.StartAsync();
		var address = host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		using var handler = new SocketsHttpHandler();
		using var httpClient = new HttpClient(handler);
		using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = httpClient });
		var client = new EventStore.Client.Monitoring.Monitoring.MonitoringClient(channel);

		var connectionError = Assert.ThrowsAsync<RpcException>(async () =>
			await client.ConnectionStatsAsync(new ConnectionStatsReq()));
		var replicationError = Assert.ThrowsAsync<RpcException>(async () =>
			await client.ReplicationStatsAsync(new ReplicationStatsReq()));
		var statsError = Assert.ThrowsAsync<RpcException>(async () =>
		{
			using var call = client.Stats(new StatsReq { RefreshTimePeriodInMs = 1000 });
			await call.ResponseStream.MoveNext();
		});

		Assert.Multiple(() =>
		{
			Assert.That(connectionError!.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));
			Assert.That(replicationError!.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));
			Assert.That(statsError!.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));
			Assert.That(connections.SnapshotCalls, Is.Zero);
			Assert.That(publisher.PublishCalls, Is.Zero);
			Assert.That(authorization.Operations, Is.EquivalentTo(new[] {
				new Operation(Operations.Node.Statistics.Read),
				new Operation(Operations.Node.Statistics.Replication),
				new Operation(Operations.Node.Statistics.Read)
			}));
		});
	}

	private static IHost CreateHost(IPublisher publisher, IConnectionStatsProvider connections,
		IAuthorizationProvider authorization) =>
		new HostBuilder()
			.ConfigureWebHost(webHost => webHost
				.UseKestrel(server => server.Listen(IPAddress.Loopback, 0,
					listenOptions => listenOptions.Protocols = HttpProtocols.Http2))
				.ConfigureServices(services =>
				{
					services.AddGrpc();
					services.AddSingleton(publisher);
					services.AddSingleton(connections);
					services.AddSingleton(authorization);
				})
				.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<ProductionMonitoringAdapter>());
				}))
			.Build();

	public sealed class ProductionMonitoringAdapter : EventStore.Client.Monitoring.Monitoring.MonitoringBase
	{
		private readonly object _service;
		private readonly Type _serviceType;

		public ProductionMonitoringAdapter(IPublisher publisher, IConnectionStatsProvider connections,
			IAuthorizationProvider authorization)
		{
			_serviceType = typeof(IConnectionStatsProvider).Assembly.GetType(
				"EventStore.Core.Services.Transport.Grpc.Monitoring", throwOnError: true)!;
			_service = Activator.CreateInstance(_serviceType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null, args: [publisher, connections, authorization], culture: null)!;
		}

		public override Task<ConnectionStatsResp> ConnectionStats(ConnectionStatsReq request, ServerCallContext context) =>
			Invoke<Task<ConnectionStatsResp>>(nameof(ConnectionStats), request, context);

		public override Task<ReplicationStatsResp> ReplicationStats(ReplicationStatsReq request, ServerCallContext context) =>
			Invoke<Task<ReplicationStatsResp>>(nameof(ReplicationStats), request, context);

		public override Task Stats(StatsReq request, IServerStreamWriter<StatsResp> responseStream,
			ServerCallContext context) =>
			Invoke<Task>(nameof(Stats), request, responseStream, context);

		private T Invoke<T>(string name, params object[] args) =>
			(T)_serviceType.GetMethod(name)!.Invoke(_service, args)!;
	}

	private sealed class CountingPublisher : IPublisher
	{
		public int PublishCalls { get; private set; }
		public void Publish(Message message)
		{
			PublishCalls++;
			throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");
		}
	}

	private sealed class ServingPublisher : IPublisher
	{
		public int PublishCalls { get; private set; }
		public void Publish(Message message)
		{
			PublishCalls++;
			switch (message)
			{
				case MonitoringMessage.GetFreshStats request:
					request.Envelope.ReplyWith(new MonitoringMessage.GetFreshStatsCompleted(
						true, new Dictionary<string, object> { ["test"] = 1 }));
					break;
				case ReplicationMessage.GetReplicationStats request:
					request.Envelope.ReplyWith(new ReplicationMessage.GetReplicationStatsCompleted([]));
					break;
				default:
					throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");
			}
		}
	}

	private sealed class CountingConnections : IConnectionStatsProvider
	{
		public int SnapshotCalls { get; private set; }
		public IReadOnlyList<ConnectionStatsSnapshot> Snapshot()
		{
			SnapshotCalls++;
			return Array.Empty<ConnectionStatsSnapshot>();
		}
	}

	private sealed class CapturingAuthorizationProvider(bool allow) : AuthorizationProviderBase
	{
		public List<Operation> Operations { get; } = new();

		public override ValueTask<bool> CheckAccessAsync(ClaimsPrincipal principal, Operation operation,
			CancellationToken cancellationToken)
		{
			Operations.Add(operation);
			return ValueTask.FromResult(allow);
		}
	}
}
