using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
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
public class ConnectionStatsKestrelTests
{
	[Test]
	public async Task grpc_connection_is_visible_while_active_and_removed_after_disconnect()
	{
		var tracker = new NodeConnectionTracker();
		using var host = new HostBuilder()
			.ConfigureWebHost(webHost => webHost
				.UseKestrel(server => server.Listen(IPAddress.Loopback, 0, listenOptions =>
				{
					listenOptions.Protocols = HttpProtocols.Http2;
					listenOptions.Use(next => context => tracker.Track(context, next, isTls: false));
				}))
				.ConfigureServices(services =>
				{
					services.AddGrpc();
					services.AddSingleton(tracker);
				})
				.Configure(app =>
				{
					app.UseRouting();
					app.Use((context, next) =>
					{
						tracker.ObserveRequest(
							context.Connection.Id,
							context.Request.Protocol,
							context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true,
							context.Request.Headers["connection-name"].FirstOrDefault(),
							context.Request.Headers.UserAgent.ToString());
						return next();
					});
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<ProductionMonitoringAdapter>());
				}))
			.Build();

		await host.StartAsync();
		var address = host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		string connectionId;

		using (var handler = new SocketsHttpHandler())
		using (var httpClient = new HttpClient(handler))
		using (var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = httpClient }))
		{
			var client = new EventStore.Client.Monitoring.Monitoring.MonitoringClient(channel);
			var response = await client.ConnectionStatsAsync(
				new ConnectionStatsReq(), deadline: DateTime.UtcNow.AddSeconds(10));
			var connection = response.Connections.Single(x => x.Application == "gRPC");
			connectionId = connection.ConnectionId;

			Assert.Multiple(() =>
			{
				Assert.That(connection.Protocol, Is.EqualTo("HTTP/2"));
				Assert.That(connection.IsTls, Is.False);
				Assert.That(connection.TotalBytesReceived, Is.GreaterThan(0));
				Assert.That(connection.RemoteEndpoint, Is.Not.Empty);
				Assert.That(connection.LocalEndpoint, Is.Not.Empty);
				Assert.That(tracker.Snapshot().Any(x => x.ConnectionId == connectionId), Is.True);
			});
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (tracker.Snapshot().Any(x => x.ConnectionId == connectionId))
		{
			await Task.Delay(25, timeout.Token);
		}
	}

	public sealed class ProductionMonitoringAdapter : EventStore.Client.Monitoring.Monitoring.MonitoringBase
	{
		private readonly object _service;
		private readonly MethodInfo _method;

		public ProductionMonitoringAdapter(NodeConnectionTracker tracker)
		{
			var serviceType = typeof(IConnectionStatsProvider).Assembly.GetType(
				"EventStore.Core.Services.Transport.Grpc.Monitoring", throwOnError: true)!;
			_service = Activator.CreateInstance(
				serviceType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				args: [new RejectingPublisher(), tracker, new AllowMonitoringAuthorizationProvider()],
				culture: null)!;
			_method = serviceType.GetMethod(nameof(ConnectionStats))!;
		}

		public override Task<ConnectionStatsResp> ConnectionStats(
			ConnectionStatsReq request, ServerCallContext context) =>
			(Task<ConnectionStatsResp>)_method.Invoke(_service, [request, context])!;
	}

	private sealed class RejectingPublisher : IPublisher
	{
		public void Publish(Message message) =>
			throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");
	}
}
