using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Monitoring.Stats;
using Grpc.Core;
using NUnit.Framework;
namespace EventStore.Core.Tests.Services.Transport.Grpc.MonitoringTests;

[TestFixture]
public class StatsRpcTests {
	[Test]
	public async Task should_preserve_flat_stats_by_default() {
		var publisher = new CapturingPublisher(
			stats: new Dictionary<string, object> {
				["es-queue-mainQueue"] = 12
			});
		var response = await ReadSingleResponse(
			publisher,
			new StatsReq {
				RefreshTimePeriodInMs = 1
			});

		Assert.IsTrue(publisher.RequestedStats);
		Assert.IsFalse(publisher.UseMetadata);
		Assert.IsFalse(publisher.UseGrouping);
		Assert.That(response.Stats["es-queue-mainQueue"], Is.EqualTo("12"));
		Assert.That(response.StructuredStats.StructValue.Fields["es-queue-mainQueue"].NumberValue, Is.EqualTo(12));
	}

	[Test]
	public async Task should_honor_stats_path_when_grouping_enabled() {
		var publisher = new CapturingPublisher(
			stats: new Dictionary<string, object> {
				["es"] = new Dictionary<string, object> {
					["queue"] = new Dictionary<string, object> {
						["mainQueue"] = 12
					}
				}
			},
			applySelector: true);
		var response = await ReadSingleResponse(
			publisher,
			new StatsReq {
				RefreshTimePeriodInMs = 1,
				StatsPath = "es/queue",
				UseGrouping = true
			});

		Assert.That(response.StructuredStats.StructValue.Fields.ContainsKey("mainQueue"));
		Assert.That(response.StructuredStats.StructValue.Fields["mainQueue"].NumberValue, Is.EqualTo(12));
	}

	[Test]
	public void should_reject_stats_path_when_grouping_is_disabled() {
		var publisher = new CapturingPublisher(new Dictionary<string, object>());

		var ex = Assert.ThrowsAsync<RpcException>(async () =>
			await ReadSingleResponse(
				publisher,
				new StatsReq {
					RefreshTimePeriodInMs = 1,
					StatsPath = "es/queue",
					UseGrouping = false
				}));

		Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
	}

	[Test]
	public async Task should_map_metadata_when_requested() {
		var publisher = new CapturingPublisher(
			stats: new Dictionary<string, object> {
				["proc"] = new Dictionary<string, object> {
					["mem"] = new StatMetadata(123L, "Process", "Process Virtual Memory")
				}
			});
		var response = await ReadSingleResponse(
			publisher,
			new StatsReq {
				RefreshTimePeriodInMs = 1,
				UseMetadata = true
			});

		Assert.IsTrue(publisher.UseMetadata);
		var metadata = response.StructuredStats.StructValue.Fields["proc"].StructValue.Fields["mem"].StructValue.Fields;
		Assert.That(metadata["value"].NumberValue, Is.EqualTo(123));
		Assert.That(metadata["category"].StringValue, Is.EqualTo("Process"));
		Assert.That(metadata["title"].StringValue, Is.EqualTo("Process Virtual Memory"));
		Assert.That(metadata["drawChart"].BoolValue, Is.True);
	}

	[Test]
	public void should_return_not_found_for_missing_stats_path() {
		var publisher = new CapturingPublisher(
			stats: new Dictionary<string, object> {
				["es"] = new Dictionary<string, object> {
					["queue"] = new Dictionary<string, object> {
						["mainQueue"] = 12
					}
				}
			},
			applySelector: true);

		var ex = Assert.ThrowsAsync<RpcException>(async () =>
			await ReadSingleResponse(
				publisher,
				new StatsReq {
					RefreshTimePeriodInMs = 1,
					StatsPath = "es/missing",
					UseGrouping = true
				}));

		Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.NotFound));
	}

	private static async Task<StatsResp> ReadSingleResponse(CapturingPublisher publisher, StatsReq request) {
		var serviceType = typeof(MonitoringMessage).Assembly.GetType(
			"EventStore.Core.Services.Transport.Grpc.Monitoring",
			throwOnError: true);
		var service = Activator.CreateInstance(
			serviceType!,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: [publisher],
			culture: null);

		using var cts = new CancellationTokenSource();
		var stream = new CapturingResponseStream(cts);
		Task task;
		try {
			task = (Task)serviceType!.GetMethod(nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.Stats))!
				.Invoke(service, [request, stream, new TestServerCallContext(cts.Token)])!;
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null) {
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw;
		}

		try {
			await task;
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested) {
		}

		return stream.SingleResponse!;
	}

	private sealed class CapturingPublisher(
		Dictionary<string, object> stats,
		bool applySelector = false) : IPublisher {
		public bool RequestedStats { get; private set; }
		public bool UseMetadata { get; private set; }
		public bool UseGrouping { get; private set; }

		public void Publish(Message message) {
			if (message is not MonitoringMessage.GetFreshStats request) {
				throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");
			}

			RequestedStats = true;
			UseMetadata = request.UseMetadata;
			UseGrouping = request.UseGrouping;

			var responseStats = applySelector ? request.StatsSelector(stats) : stats;
			request.Envelope.ReplyWith(
				new MonitoringMessage.GetFreshStatsCompleted(
					success: responseStats is not null,
					stats: responseStats));
		}
	}

	private sealed class CapturingResponseStream(CancellationTokenSource cts) : IServerStreamWriter<StatsResp> {
		public StatsResp SingleResponse { get; private set; }
		public WriteOptions WriteOptions { get; set; }

		public Task WriteAsync(StatsResp message) {
			SingleResponse = message;
			cts.Cancel();
			return Task.CompletedTask;
		}
	}

	private sealed class TestServerCallContext(CancellationToken cancellationToken) : ServerCallContext {
		protected override string MethodCore => nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.Stats);
		protected override string HostCore => "localhost";
		protected override string PeerCore => "ipv4:127.0.0.1:0";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => cancellationToken;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions WriteOptionsCore { get; set; }
		protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
			throw new NotSupportedException();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
	}
}
