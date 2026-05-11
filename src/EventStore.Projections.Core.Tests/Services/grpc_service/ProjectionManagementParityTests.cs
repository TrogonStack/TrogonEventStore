using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Projections;
using EventStore.Common.Utils;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.grpc_service;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class ProjectionManagementParityTests<TLogFormat, TStreamId> : SpecificationWithNodeAndProjectionSubsystem<TLogFormat, TStreamId>
{
	private const string ProjectionName = "grpc-parity-projection";
	private const string DisabledOneTimeProjectionName = "grpc-parity-one-time";
	private const string TransientProjectionName = "grpc-parity-transient";
	private const string DebugStream = "grpc-parity-stream";

	private GrpcChannel _channel;
	private Client.Projections.Projections.ProjectionsClient _client;
	private GetQueryResp _query;
	private GetQueryResp _disabledOneTimeQuery;
	private GetConfigResp _initialConfig;
	private GetConfigResp _updatedConfig;
	private ReadEventsResp _feedPage;
	private StatisticsResp.Types.Details[] _allNonTransientStatistics;

	public override async Task Given()
	{
		_channel = GrpcChannel.ForAddress(
			new Uri($"https://{_node.HttpEndPoint}"),
			new GrpcChannelOptions
			{
				HttpHandler = _node.HttpMessageHandler
			});
		_client = new Client.Projections.Projections.ProjectionsClient(_channel);

		await _client.CreateAsync(new CreateReq
		{
			Options = new CreateReq.Types.Options
			{
				Continuous = new CreateReq.Types.Options.Types.Continuous
				{
					Name = ProjectionName,
					EmitEnabled = true,
					TrackEmittedStreams = true
				},
				Query = "something invalid"
			}
		}, GetCallOptions());

		await _client.CreateAsync(new CreateReq
		{
			Options = new CreateReq.Types.Options
			{
				OneTime = new EventStore.Client.Empty(),
				Enabled = false,
				Name = DisabledOneTimeProjectionName,
				CheckpointsEnabled = true,
				EmitEnabled = false,
				TrackEmittedStreams = false,
				Query = "fromAll().when({$any:function(s,e){return s;}});"
			}
		}, GetCallOptions());

		await _client.CreateAsync(new CreateReq
		{
			Options = new CreateReq.Types.Options
			{
				Transient = new CreateReq.Types.Options.Types.Transient
				{
					Name = TransientProjectionName
				},
				Query = $"fromStream(\"{DebugStream}\").when({{$any:function(s,e){{return s;}}}});"
			}
		}, GetCallOptions());

		await PostEvent(DebugStream, "type1", "{\"value\":1}");
		await PostEvent(DebugStream, "type2", "{\"value\":2}");
	}

	public override async Task When()
	{
		_query = await _client.GetQueryAsync(new GetQueryReq
		{
			Options = new GetQueryReq.Types.Options
			{
				Name = ProjectionName
			}
		}, GetCallOptions());

		_disabledOneTimeQuery = await _client.GetQueryAsync(new GetQueryReq
		{
			Options = new GetQueryReq.Types.Options
			{
				Name = DisabledOneTimeProjectionName
			}
		}, GetCallOptions());

		_initialConfig = await _client.GetConfigAsync(new GetConfigReq
		{
			Options = new GetConfigReq.Types.Options
			{
				Name = ProjectionName
			}
		}, GetCallOptions());

		await WaitForStatus(ProjectionName, "Faulted");

		await _client.UpdateConfigAsync(new UpdateConfigReq
		{
			Options = new UpdateConfigReq.Types.Options
			{
				Name = ProjectionName,
				EmitEnabled = _initialConfig.Details.EmitEnabled,
				TrackEmittedStreams = _initialConfig.Details.TrackEmittedStreams,
				CheckpointAfterMs = 1750,
				CheckpointHandledThreshold = 18,
				CheckpointUnhandledBytesThreshold = 4096,
				PendingEventsThreshold = 42,
				MaxWriteBatchLength = 17,
				MaxAllowedWritesInFlight = 6,
				ProjectionExecutionTimeout = 11
			}
		}, GetCallOptions());

		_updatedConfig = await _client.GetConfigAsync(new GetConfigReq
		{
			Options = new GetConfigReq.Types.Options
			{
				Name = ProjectionName
			}
		}, GetCallOptions());

		_feedPage = await _client.ReadEventsAsync(new ReadEventsReq
		{
			Options = new ReadEventsReq.Types.Options
			{
				QuerySourcesJson = new QuerySourcesDefinition
				{
					Streams = new[] { DebugStream },
					AllEvents = true
				}.ToJson(),
				PositionJson = CheckpointTag.FromStreamPosition(0, DebugStream, -1).ToJsonRaw().ToString(),
				MaxEvents = 10
			}
		}, GetCallOptions());

		await _client.AbortAsync(new AbortReq
		{
			Options = new AbortReq.Types.Options
			{
				Name = TransientProjectionName
			}
		}, GetCallOptions());

		_allNonTransientStatistics = await ReadStatisticsAsync(new StatisticsReq
		{
			Options = new StatisticsReq.Types.Options
			{
				AllNonTransient = new EventStore.Client.Empty()
			}
		});
	}

	[Test]
	public void get_query_returns_projection_metadata()
	{
		Assert.That(_query.Details.Name, Is.EqualTo(ProjectionName));
		Assert.That(_query.Details.Query, Is.EqualTo("something invalid"));
		Assert.That(_query.Details.EmitEnabled, Is.True);
		Assert.That(_query.Details.TrackEmittedStreams, Is.True);
		Assert.That(_query.Details.CheckpointsEnabled, Is.True);
		Assert.That(_query.Details.DefinitionJson, Is.Not.Empty);
		Assert.That(_query.Details.OutputConfigJson, Is.Not.Empty);
	}

	[Test]
	public void create_ignores_track_emitted_streams_when_emit_is_disabled()
	{
		Assert.That(_disabledOneTimeQuery.Details.EmitEnabled, Is.False);
		Assert.That(_disabledOneTimeQuery.Details.TrackEmittedStreams, Is.False);
	}

	[Test]
	public void update_config_round_trips_through_grpc()
	{
		Assert.That(_updatedConfig.Details.CheckpointAfterMs, Is.EqualTo(1750));
		Assert.That(_updatedConfig.Details.CheckpointHandledThreshold, Is.EqualTo(18));
		Assert.That(_updatedConfig.Details.CheckpointUnhandledBytesThreshold, Is.EqualTo(4096));
		Assert.That(_updatedConfig.Details.PendingEventsThreshold, Is.EqualTo(42));
		Assert.That(_updatedConfig.Details.MaxWriteBatchLength, Is.EqualTo(17));
		Assert.That(_updatedConfig.Details.MaxAllowedWritesInFlight, Is.EqualTo(6));
		Assert.That(_updatedConfig.Details.ProjectionExecutionTimeout, Is.EqualTo(11));
	}

	[Test]
	public void read_events_returns_feed_page_details()
	{
		Assert.That(_feedPage.Details.CorrelationId, Is.Not.Empty);
		Assert.That(_feedPage.Details.ReaderPositionJson, Is.Not.Empty);
		Assert.That(_feedPage.Details.Events.Count, Is.GreaterThanOrEqualTo(2));
		Assert.That(_feedPage.Details.Events.Any(x => x.EventStreamId == DebugStream && x.EventType == "type1"), Is.True);
		Assert.That(_feedPage.Details.Events.Any(x => x.EventStreamId == DebugStream && x.EventType == "type2"), Is.True);
	}

	[Test]
	public void all_non_transient_statistics_excludes_transient_projections()
	{
		Assert.That(_allNonTransientStatistics.Any(x => x.Name == ProjectionName), Is.True);
		Assert.That(_allNonTransientStatistics.Any(x => x.Name == DisabledOneTimeProjectionName), Is.True);
		Assert.That(_allNonTransientStatistics.Any(x => x.Name == TransientProjectionName), Is.False);
	}

	[OneTimeTearDown]
	public override Task TestFixtureTearDown()
	{
		_channel?.Dispose();
		return base.TestFixtureTearDown();
	}

	private static CallOptions GetCallOptions(TimeSpan? deadline = null)
	{
		var credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});

		return new CallOptions(
			credentials: credentials,
			deadline: deadline is { } value
				? DateTime.UtcNow.Add(value)
				: null);
	}

	private async Task WaitForStatus(string projectionName, string expectedStatus)
	{
		for (var attempt = 0; attempt < 40; attempt++)
		{
			using var statistics = _client.Statistics(new StatisticsReq
			{
				Options = new StatisticsReq.Types.Options
				{
					Name = projectionName
				}
			}, GetCallOptions(TimeSpan.FromSeconds(10)));

			while (await statistics.ResponseStream.MoveNext(default))
			{
				if (statistics.ResponseStream.Current.Details.Status == expectedStatus)
				{
					return;
				}
			}

			await Task.Delay(250);
		}

		Assert.Fail($"Projection '{projectionName}' never reached status '{expectedStatus}'.");
	}

	private async Task<StatisticsResp.Types.Details[]> ReadStatisticsAsync(StatisticsReq request)
	{
		using var statistics = _client.Statistics(request, GetCallOptions(TimeSpan.FromSeconds(10)));
		var results = new System.Collections.Generic.List<StatisticsResp.Types.Details>();
		while (await statistics.ResponseStream.MoveNext(default))
		{
			results.Add(statistics.ResponseStream.Current.Details);
		}

		return results.ToArray();
	}
}
