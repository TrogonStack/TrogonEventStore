using System;
using System.Net.Http;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using Grpc.Health.V1;
using Grpc.Net.Client;
using NUnit.Framework;

namespace EventStore.Core.Tests.Http.HealthChecks;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_performing_health_probes<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture {
	private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);
	private MiniNode<TLogFormat, TStreamId> _node;
	private bool _nodeStarted;
	[SetUp]
	public void SetUp() {
		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
	}

	[TearDown]
	public async Task Teardown() {
		if (_nodeStarted) {
			await _node.Shutdown();
		}
	}

	private static readonly object[] MethodAllowedTestCases = {
		new object[] {HttpMethod.Head},
		new object[] {HttpMethod.Get},
	};

	[TestCaseSource(nameof(MethodAllowedTestCases))]
	public async Task readiness_before_start_returns_error(HttpMethod method) {
		using var response = await _node.HttpClient.SendAsync(new HttpRequestMessage(method, "/-/readiness"));
		_nodeStarted = false; //just for clarity
		Assert.GreaterOrEqual((int)response.StatusCode, 500);
	}

	[TestCaseSource(nameof(MethodAllowedTestCases))]
	public async Task readiness_after_start_returns_success(HttpMethod method) {
		await StartNodeAndWaitForReadiness();
		using var response = await _node.HttpClient.SendAsync(new HttpRequestMessage(method, "/-/readiness") {
			Version = new Version(2, 0)
		});

		Assert.GreaterOrEqual((int)response.StatusCode, 200);
		Assert.Less((int)response.StatusCode, 400);
	}

	[Test]
	public async Task grpc_health_reports_serving_after_start() {
		await StartNodeAndWaitForReadiness();

		using var channel = GrpcChannel.ForAddress(new Uri($"https://{_node.HttpEndPoint}"),
			new GrpcChannelOptions {
				HttpClient = _node.HttpClient,
				DisposeHttpClient = false,
			});
		var client = new Health.HealthClient(channel);

		var readiness = await client.CheckAsync(new HealthCheckRequest { Service = "readiness" });
		var liveness = await client.CheckAsync(new HealthCheckRequest { Service = "liveness" });

		Assert.AreEqual(HealthCheckResponse.Types.ServingStatus.Serving, readiness.Status);
		Assert.AreEqual(HealthCheckResponse.Types.ServingStatus.Serving, liveness.Status);
	}

	[TestCaseSource(nameof(MethodAllowedTestCases))]
	public async Task liveness_before_start_returns_success(HttpMethod method) {
		using var response = await _node.HttpClient.SendAsync(new HttpRequestMessage(method, "/-/liveness"));
		_nodeStarted = false;
		Assert.GreaterOrEqual((int)response.StatusCode, 200);
		Assert.Less((int)response.StatusCode, 400);
	}

	[TestCaseSource(nameof(MethodAllowedTestCases))]
	public async Task liveness_after_start_returns_success(HttpMethod method) {
		await StartNodeAndWaitForReadiness();
		using var response = await _node.HttpClient.SendAsync(new HttpRequestMessage(method, "/-/liveness") {
			Version = new Version(2, 0)
		});

		Assert.GreaterOrEqual((int)response.StatusCode, 200);
		Assert.Less((int)response.StatusCode, 400);
	}

	[TestCaseSource(nameof(MethodAllowedTestCases))]
	public async Task readiness_after_shutdown_returns_error(HttpMethod method) {
		await StartNodeAndWaitForReadiness();
		await _node.Node.StopAsync()
			.WithTimeout();

		using var response = await _node.HttpClient.SendAsync(new HttpRequestMessage(method, "/-/readiness"));

		Assert.GreaterOrEqual((int)response.StatusCode, 500);
	}

	[TestCaseSource(nameof(MethodAllowedTestCases))]
	public async Task liveness_after_shutdown_returns_success(HttpMethod method) {
		await StartNodeAndWaitForReadiness();
		await _node.Node.StopAsync()
			.WithTimeout();

		using var response = await _node.HttpClient.SendAsync(new HttpRequestMessage(method, "/-/liveness"));

		Assert.GreaterOrEqual((int)response.StatusCode, 200);
		Assert.Less((int)response.StatusCode, 400);
	}

	private async Task StartNodeAndWaitForReadiness() {
		await _node.Start();
		_nodeStarted = true;
		await _node.WaitForTcpEndPoint().WithTimeout(ReadinessTimeout);

		using var connection = await TestConnectionLifecycle.ReconnectUntilReady(
			() => TestConnection.CreateMiniNodeClient(_node.TcpEndPoint),
			conn => conn.ReadAllEventsForwardAsync(Position.Start, 1, false, DefaultData.AdminCredentials),
			ReadinessTimeout);
	}
}
