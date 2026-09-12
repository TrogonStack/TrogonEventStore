using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Tests.Helpers;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using ILogger = Serilog.ILogger;
using ReadEvent = EventStore.Client.Streams.ReadResp.Types.ReadEvent;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Core.Tests.Services.Storage.Scavenge;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_running_scavenge_from_storage_scavenger<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private static readonly ILogger Log = Serilog.Log.ForContext<when_running_scavenge_from_storage_scavenger<TLogFormat, TStreamId>>();
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
	private MiniNode<TLogFormat, TStreamId> _node;
	private List<ReadEvent> _result;

	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
		await _node.Start();

		var scavengeMessage =
			new ClientMessage.ScavengeDatabase(new NoopEnvelope(), Guid.NewGuid(), SystemAccounts.System, 0, 1, null, null, false);
		_node.Node.MainQueue.Publish(scavengeMessage);

		try
		{
			await When().WithTimeout();
		}
		catch (Exception ex)
		{
			throw new Exception("When Failed", ex);
		}
	}

	[TearDown]
	public async Task TearDown()
	{
		await _node.Shutdown();
	}

	public async Task When()
	{
		using var channel = GrpcChannel.ForAddress(new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions { HttpClient = _node.HttpClient, DisposeHttpClient = false });
		var client = new StreamsClient(channel);
		_result = new List<ReadEvent>();
		var deadline = DateTime.UtcNow + Timeout;
		while (_result.Count < 2 && DateTime.UtcNow < deadline)
		{
			_result.Clear();
			using var call = client.Read(new ReadReq
			{
				Options = new()
				{
					Stream = new()
					{
						StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(SystemStreams.ScavengesStream) },
						Start = new()
					},
					ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
					ResolveLinks = true,
					Count = 2,
					NoFilter = new(),
					UuidOption = new() { Structured = new() }
				}
			}, new CallOptions(credentials: CallCredentials.FromInterceptor((_, metadata) =>
			{
				metadata.Add("authorization",
					$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
				return Task.CompletedTask;
			})));
			while (await call.ResponseStream.MoveNext(default))
				if (call.ResponseStream.Current.Event is { } resolvedEvent)
					_result.Add(resolvedEvent);

			if (_result.Count < 2)
			{
				Log.Information("Waiting for the scavenge event stream.");
				await Task.Delay(100);
			}
		}

		Assert.That(_result.Count, Is.GreaterThanOrEqualTo(2), "Timeout expired while waiting for events.");
	}

	[Test]
	public void should_create_scavenge_started_event_on_index_stream()
	{
		var scavengeStartedEvent =
			_result.FirstOrDefault(x => x.Event.Metadata[GrpcMetadata.Type] == SystemEventTypes.ScavengeStarted);
		Assert.IsNotNull(scavengeStartedEvent);
	}

	[Test]
	public void should_create_scavenge_completed_event_on_index_stream()
	{
		var scavengeCompletedEvent =
			_result.FirstOrDefault(x => x.Event.Metadata[GrpcMetadata.Type] == SystemEventTypes.ScavengeCompleted);
		Assert.IsNotNull(scavengeCompletedEvent);
	}

	[Test]
	public void should_link_started_and_completed_events_to_the_same_stream()
	{
		var scavengeStartedEvent =
			_result.FirstOrDefault(x => x.Event.Metadata[GrpcMetadata.Type] == SystemEventTypes.ScavengeStarted);
		var scavengeCompletedEvent =
			_result.FirstOrDefault(x => x.Event.Metadata[GrpcMetadata.Type] == SystemEventTypes.ScavengeCompleted);
		Assert.AreEqual(scavengeStartedEvent.Event.StreamIdentifier, scavengeCompletedEvent.Event.StreamIdentifier);
	}
}
