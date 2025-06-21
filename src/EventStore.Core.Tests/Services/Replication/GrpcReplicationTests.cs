using System;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Messages;
using EventStore.Core.Metrics;
using EventStore.Core.Services.Transport.Grpc.Cluster;
using EventStore.Plugins.Authorization;
using NUnit.Framework;
using EventStore.Core.Tests.Helpers;

namespace EventStore.Core.Tests.Services.Replication {
	[TestFixture]
	public class GrpcReplicationTests {
		[Test]
		public void can_create_grpc_replication_service() {
			var bus = new InMemoryBus("test");
			var authProvider = new PassthroughAuthorizationProvider();
			var queueTrackers = new QueueTrackers();
			var clusterDns = "test.cluster";

			var replicationService = new Replication(bus, authProvider, queueTrackers, clusterDns);

			Assert.That(replicationService, Is.Not.Null);
		}

		[Test]
		public void can_call_get_replication_info() {
			var bus = new InMemoryBus("test");
			var authProvider = new PassthroughAuthorizationProvider();
			var queueTrackers = new QueueTrackers();
			var clusterDns = "test.cluster";

			var replicationService = new Replication(bus, authProvider, queueTrackers, clusterDns);

			// This should not throw
			Assert.DoesNotThrow(() => {
				var context = new MockServerCallContext();
				var result = replicationService.GetReplicationInfo(new EventStore.Client.Empty(), context);
				Assert.That(result, Is.Not.Null);
			});
		}
	}

	// Simple mock implementation for testing
	public class MockServerCallContext : Grpc.Core.ServerCallContext {
		public override string Method => "Test";
		public override string Host => "test.host";
		public override string Peer => "test.peer";
		public override DateTime Deadline => DateTime.UtcNow.AddMinutes(1);
		public override Grpc.Core.Metadata RequestHeaders => new Grpc.Core.Metadata();
		public override System.Threading.CancellationToken CancellationToken => System.Threading.CancellationToken.None;
		public override Grpc.Core.Metadata ResponseTrailers => new Grpc.Core.Metadata();
		public override Grpc.Core.Status Status { get; set; }
		public override Grpc.Core.WriteOptions WriteOptions { get; set; }

		protected override string MethodCore => Method;
		protected override string HostCore => Host;
		protected override string PeerCore => Peer;
		protected override DateTime DeadlineCore => Deadline;
		protected override Grpc.Core.Metadata RequestHeadersCore => RequestHeaders;
		protected override System.Threading.CancellationToken CancellationTokenCore => CancellationToken;
		protected override Grpc.Core.Metadata ResponseTrailersCore => ResponseTrailers;
		protected override Grpc.Core.Status StatusCore { get => Status; set => Status = value; }
		protected override Grpc.Core.WriteOptions WriteOptionsCore { get => WriteOptions; set => WriteOptions = value; }

		protected override System.Threading.Tasks.Task WriteResponseHeadersAsyncCore(Grpc.Core.Metadata responseHeaders) {
			return System.Threading.Tasks.Task.CompletedTask;
		}

		protected override Grpc.Core.ContextPropagationToken CreatePropagationTokenCore(Grpc.Core.ContextPropagationOptions options) {
			return null;
		}

		public override Microsoft.AspNetCore.Http.HttpContext GetHttpContext() {
			var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
			httpContext.User = new System.Security.Claims.ClaimsPrincipal();
			return httpContext;
		}
	}
}