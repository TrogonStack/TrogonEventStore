using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Streams;
using EventStore.Common.Options;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Util;
using EventStore.Projections.Core.Services.Processing;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using StatisticsReq = EventStore.Client.Projections.StatisticsReq;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Projections.Core.Tests.ClientAPI;

[Category("Grpc")]
public abstract class specification_with_standard_projections_runnning<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected sealed class StreamReadResult
	{
		public StreamReadResult(bool exists, IReadOnlyList<ReadResp.Types.ReadEvent> events)
		{
			Exists = exists;
			Events = events;
		}

		public bool Exists { get; }
		public IReadOnlyList<ReadResp.Types.ReadEvent> Events { get; }
	}

	private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
	private GrpcChannel _streamChannel;
	private StreamsClient _streams;
	private Task _projectionsCreated;
	private ProjectionsSubsystem _projections;
	private MiniNode<TLogFormat, TStreamId> _node;

	private protected ProjectionManagementTestClient ProjectionClient;
	protected virtual TimeSpan StartupTimeout => TimeSpan.FromMinutes(5);

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		var configuration = new ProjectionSubsystemOptions(
			GivenWorkerThreadCount(),
			ProjectionType.All,
			false,
			TimeSpan.FromMinutes(Opts.ProjectionsQueryExpiryDefault),
			Opts.FaultOutOfOrderProjectionsDefault,
			500,
			250);
		_projections = new ProjectionsSubsystem(configuration);
		_node = new MiniNode<TLogFormat, TStreamId>(
			PathName,
			subsystems: [_projections]);
		_projectionsCreated = SystemProjections.Created(_projections.LeaderInputBus);

		await _node.Start(StartupTimeout);
		await _node.AdminUserCreated.WithTimeout(StartupTimeout);
		await _projectionsCreated.WithTimeout(OperationTimeout);

		_streamChannel = GrpcChannel.ForAddress(
			_node.HttpClient.BaseAddress ?? new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions
			{
				HttpClient = _node.HttpClient,
				DisposeHttpClient = false
			});
		_streams = new StreamsClient(_streamChannel);
		ProjectionClient = new ProjectionManagementTestClient(_node.HttpEndPoint, _node.HttpMessageHandler);

		if (GivenStandardProjectionsRunning())
		{
			await EnableStandardProjections();
		}

		try
		{
			await Given().WithTimeout(OperationTimeout);
		}
		catch (Exception ex)
		{
			throw new Exception("Given Failed", ex);
		}

		try
		{
			await When().WithTimeout(OperationTimeout);
		}
		catch (Exception ex)
		{
			throw new Exception("When Failed", ex);
		}
	}

	protected virtual int GivenWorkerThreadCount() => 1;

	[TearDown]
	public async Task PostTestAsserts()
	{
		var all = await ProjectionClient.StatisticsAll();
		if (all.Any(p => p.Status == "Faulted"))
		{
			Assert.Fail("Projections faulted while running the test" + "\r\n" + string.Join("\r\n", all));
		}
	}

	protected async Task EnableStandardProjections()
	{
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.EventByCategoryStandardProjection);
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.EventByTypeStandardProjection);
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.StreamByCategoryStandardProjection);
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.StreamsStandardProjection);
	}

	protected async Task DisableStandardProjections()
	{
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.EventByCategoryStandardProjection);
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.EventByTypeStandardProjection);
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.StreamByCategoryStandardProjection);
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.StreamsStandardProjection);
	}

	protected virtual bool GivenStandardProjectionsRunning() => true;

	protected async Task EnableProjection(string name)
	{
		await ProjectionClient.Enable(name);
		await WaitForProjectionStatus(name, status => status.Contains("Running", StringComparison.OrdinalIgnoreCase));
	}

	protected async Task DisableProjection(string name)
	{
		await ProjectionClient.Disable(name);
		await WaitForProjectionStatus(name, status => status.Contains("Stopped", StringComparison.OrdinalIgnoreCase));
	}

	protected async Task AbortProjection(string name)
	{
		await ProjectionClient.Abort(name);
		await WaitForProjectionStatus(name, status => status.Contains("Stopped", StringComparison.OrdinalIgnoreCase));
	}

	protected async Task CreateContinuousProjection(string name, string query)
	{
		await ProjectionClient.CreateContinuous(name, query);
		await WaitForProjectionStatus(name, status => status.Contains("Running", StringComparison.OrdinalIgnoreCase));
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		ProjectionClient?.Dispose();
		_streamChannel?.Dispose();

		if (_node != null)
		{
			await _node.Shutdown();
		}

		await base.TestFixtureTearDown();
	}

	protected virtual Task When() => Task.CompletedTask;

	protected virtual Task Given() => Task.CompletedTask;

	protected async Task PostEvent(string stream, string eventType, string data)
	{
		await Append(stream, eventType, data, new AppendReq.Types.Options { Any = new Empty() });
	}

	protected Task<AppendResp> AppendToNewStream(string stream, string eventType, string data) =>
		Append(stream, eventType, data, new AppendReq.Types.Options { NoStream = new Empty() });

	protected Task<AppendResp> AppendToStream(
		string stream,
		ulong expectedRevision,
		string eventType,
		string data) =>
		Append(stream, eventType, data, new AppendReq.Types.Options { Revision = expectedRevision });

	protected Task HardDeleteStream(string stream) =>
		Tombstone(stream, new TombstoneReq.Types.Options { Any = new Empty() });

	protected Task HardDeleteStream(string stream, ulong expectedRevision) =>
		Tombstone(stream, new TombstoneReq.Types.Options { Revision = expectedRevision });

	protected Task SoftDeleteStream(string stream) =>
		Delete(stream, new DeleteReq.Types.Options { Any = new Empty() });

	protected Task SoftDeleteStream(string stream, ulong expectedRevision) =>
		Delete(stream, new DeleteReq.Types.Options { Revision = expectedRevision });

	protected void WaitIdle(int multiplier = 1)
	{
		_node.WaitIdle();
		Thread.Sleep(TimeSpan.FromMilliseconds(50 * multiplier));
	}

	protected async Task AssertStreamTail(string streamId, params string[] events)
	{
		string[] actual = [];
		for (var attempt = 0; attempt < 80; attempt++)
		{
			var result = await ReadStream(streamId, (ulong)events.Length, false, true);
			actual = result.Events
				.Reverse()
				.Select(FormatEvent)
				.ToArray();

			if (result.Exists && actual.SequenceEqual(events))
			{
				return;
			}

			await Task.Delay(250);
		}

		Assert.Fail(
			$"Stream '{streamId}' did not reach the expected tail. Expected: [{string.Join(", ", events)}]. Actual: [{string.Join(", ", actual)}].");
	}

	protected async Task<StreamReadResult> ReadStreamForward(string streamId, ulong count, bool resolveLinks) =>
		await ReadStream(streamId, count, resolveLinks, false);

	protected async Task<StreamReadResult> WaitForStreamEvents(
		string streamId,
		int minimumEventCount,
		bool resolveLinks)
	{
		StreamReadResult result = null;
		for (var attempt = 0; attempt < 80; attempt++)
		{
			result = await ReadStreamForward(streamId, 100, resolveLinks);
			if (result.Exists && result.Events.Count >= minimumEventCount)
			{
				return result;
			}

			await Task.Delay(250);
		}

		Assert.Fail(
			$"Stream '{streamId}' did not contain {minimumEventCount} events. Actual: {result?.Events.Count ?? 0}.");
		return result;
	}

	protected async Task DumpStream(string streamId)
	{
		var result = await ReadStreamForward(streamId, 100, true);
		TestContext.Progress.WriteLine(
			$"Stream '{streamId}': {string.Join(", ", result.Events.Select(FormatEvent))}");
	}

	protected async Task PostProjection(string query)
	{
		await CreateContinuousProjection("test-projection", query);
	}

	private async Task<AppendResp> Append(
		string stream,
		string eventType,
		string data,
		AppendReq.Types.Options options)
	{
		options.StreamIdentifier = new StreamIdentifier
		{
			StreamName = ByteString.CopyFromUtf8(stream)
		};

		using var call = _streams.Append(GetCallOptions());
		await call.RequestStream.WriteAsync(new AppendReq { Options = options });
		await call.RequestStream.WriteAsync(new AppendReq
		{
			ProposedMessage = new AppendReq.Types.ProposedMessage
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFromUtf8(data),
				CustomMetadata = ByteString.Empty,
				Metadata =
				{
					{ GrpcMetadata.Type, eventType },
					{ GrpcMetadata.ContentType, GrpcMetadata.ContentTypes.ApplicationJson }
				}
			}
		});
		await call.RequestStream.CompleteAsync();
		return await call.ResponseAsync;
	}

	private async Task Delete(string stream, DeleteReq.Types.Options options)
	{
		options.StreamIdentifier = new StreamIdentifier
		{
			StreamName = ByteString.CopyFromUtf8(stream)
		};
		await _streams.DeleteAsync(new DeleteReq { Options = options }, GetCallOptions());
	}

	private async Task Tombstone(string stream, TombstoneReq.Types.Options options)
	{
		options.StreamIdentifier = new StreamIdentifier
		{
			StreamName = ByteString.CopyFromUtf8(stream)
		};
		await _streams.TombstoneAsync(new TombstoneReq { Options = options }, GetCallOptions());
	}

	private async Task<StreamReadResult> ReadStream(
		string streamId,
		ulong count,
		bool resolveLinks,
		bool backwards)
	{
		var stream = new ReadReq.Types.Options.Types.StreamOptions
		{
			StreamIdentifier = new StreamIdentifier
			{
				StreamName = ByteString.CopyFromUtf8(streamId)
			}
		};
		if (backwards)
		{
			stream.End = new Empty();
		}
		else
		{
			stream.Start = new Empty();
		}

		using var call = _streams.Read(new ReadReq
		{
			Options = new ReadReq.Types.Options
			{
				Stream = stream,
				ReadDirection = backwards
					? ReadReq.Types.Options.Types.ReadDirection.Backwards
					: ReadReq.Types.Options.Types.ReadDirection.Forwards,
				ResolveLinks = resolveLinks,
				Count = count,
				NoFilter = new Empty(),
				UuidOption = new ReadReq.Types.Options.Types.UUIDOption
				{
					Structured = new Empty()
				},
				ControlOption = new ReadReq.Types.Options.Types.ControlOption
				{
					Compatibility = 21
				}
			}
		}, GetCallOptions());

		var exists = true;
		var events = new List<ReadResp.Types.ReadEvent>();
		while (await call.ResponseStream.MoveNext(CancellationToken.None))
		{
			switch (call.ResponseStream.Current.ContentCase)
			{
				case ReadResp.ContentOneofCase.Event:
					events.Add(call.ResponseStream.Current.Event);
					break;
				case ReadResp.ContentOneofCase.StreamNotFound:
					exists = false;
					break;
			}
		}

		return new StreamReadResult(exists, events);
	}

	private async Task WaitForProjectionStatus(string name, Func<string, bool> predicate)
	{
		string lastStatus = null;
		for (var attempt = 0; attempt < 80; attempt++)
		{
			var statistics = await ProjectionClient.Statistics(new StatisticsReq.Types.Options
			{
				Name = name
			});
			lastStatus = statistics.SingleOrDefault()?.Status;
			if (lastStatus != null && predicate(lastStatus))
			{
				return;
			}

			await Task.Delay(250);
		}

		Assert.Fail($"Projection '{name}' did not reach the expected status. Last status: '{lastStatus}'.");
	}

	private static string FormatEvent(ReadResp.Types.ReadEvent readEvent)
	{
		var recordedEvent = readEvent.Event ?? readEvent.Link;
		return $"{recordedEvent.EventType()}:{recordedEvent.DebugDataView()}";
	}

	private static CallOptions GetCallOptions()
	{
		var credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});

		return new CallOptions(
			credentials: credentials,
			deadline: DateTime.UtcNow.Add(OperationTimeout));
	}
}
