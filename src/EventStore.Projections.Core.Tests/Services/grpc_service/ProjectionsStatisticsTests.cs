using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Projections;
using EventStore.Projections.Core.Tests.ClientAPI.projectionsManager;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.grpc_service;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class ProjectionsStatisticsTests<TLogFormat, TStreamId> : SpecificationWithNodeAndProjectionsManager<TLogFormat, TStreamId>
{
	private const string ExpectedRunningProjectionPosition = "C:0/P:-1";

	private GrpcChannel _channel;
	private Client.Projections.Projections.ProjectionsClient _client;
	private StatisticsResp.Types.Details _faultedProjection;
	private StatisticsResp.Types.Details _runningProjection;

	public override async Task Given()
	{
		_channel = GrpcChannel.ForAddress(
			new Uri($"https://{_node.HttpEndPoint}"),
			new GrpcChannelOptions {
				HttpHandler = _node.HttpMessageHandler
			});
		_client = new Client.Projections.Projections.ProjectionsClient(_channel);

		await _client.CreateAsync(new CreateReq {
			Options = new CreateReq.Types.Options {
				Continuous = new CreateReq.Types.Options.Types.Continuous {
					Name = "faulted-projection",
					EmitEnabled = false,
					TrackEmittedStreams = false
				},
				Query = "something invalid"
			}
		}, GetCallOptions());

		await _client.CreateAsync(new CreateReq {
			Options = new CreateReq.Types.Options {
				Continuous = new CreateReq.Types.Options.Types.Continuous {
					Name = "running-projection",
					EmitEnabled = false,
					TrackEmittedStreams = false
				},
				Query = "fromAll().when({})"
			}
		}, GetCallOptions());
	}

	public override async Task When()
	{
		for (var attempt = 0; attempt < 20; attempt++) {
			(_faultedProjection, _runningProjection) = await ReadStatisticsAsync();
			if (_faultedProjection?.Status == "Faulted"
				&& _runningProjection?.Status == "Running"
				&& _runningProjection.Position == ExpectedRunningProjectionPosition
				&& _runningProjection.LastCheckpoint == ExpectedRunningProjectionPosition)
				return;

			await Task.Delay(250);
		}

		Assert.Fail("Failed to observe both projection statistics in time");
	}

	[Test]
	public void faulted_projection_returns_safe_string_values()
	{
		Assert.NotNull(_faultedProjection);
		Assert.AreEqual("faulted-projection", _faultedProjection.EffectiveName);
		Assert.AreEqual("Faulted", _faultedProjection.Status);
		Assert.AreEqual(string.Empty, _faultedProjection.CheckpointStatus);
		Assert.AreEqual(string.Empty, _faultedProjection.Position);
		Assert.AreEqual(string.Empty, _faultedProjection.LastCheckpoint);
	}

	[Test]
	public void running_projection_keeps_its_statistics()
	{
		Assert.NotNull(_runningProjection);
		Assert.AreEqual("running-projection", _runningProjection.EffectiveName);
		Assert.AreEqual("Running", _runningProjection.Status);
		Assert.AreEqual(string.Empty, _runningProjection.CheckpointStatus);
		Assert.AreEqual(ExpectedRunningProjectionPosition, _runningProjection.Position);
		Assert.AreEqual(ExpectedRunningProjectionPosition, _runningProjection.LastCheckpoint);
	}

	[OneTimeTearDown]
	public override Task TestFixtureTearDown()
	{
		_channel?.Dispose();
		return base.TestFixtureTearDown();
	}

	private static CallOptions GetCallOptions() {
		var credentials = CallCredentials.FromInterceptor((_, metadata) => {
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});

		return new CallOptions(
			credentials: credentials,
			deadline: Debugger.IsAttached
				? DateTime.UtcNow.AddDays(1)
				: null);
	}

	private async Task<(StatisticsResp.Types.Details faultedProjection, StatisticsResp.Types.Details runningProjection)>
		ReadStatisticsAsync()
	{
		StatisticsResp.Types.Details faultedProjection = null;
		StatisticsResp.Types.Details runningProjection = null;

		using var statistics = _client.Statistics(new StatisticsReq {
			Options = new StatisticsReq.Types.Options {
				All = new Empty()
			}
		}, GetCallOptions());

		while (await statistics.ResponseStream.MoveNext(CancellationToken.None)) {
			var details = statistics.ResponseStream.Current.Details;
			if (details.Name == "faulted-projection")
				faultedProjection = details;
			if (details.Name == "running-projection")
				runningProjection = details;
		}

		return (faultedProjection, runningProjection);
	}
}
