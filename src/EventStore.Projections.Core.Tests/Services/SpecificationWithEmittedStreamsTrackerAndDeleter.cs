using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Helpers;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Emitting;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using ReadEvent = EventStore.Client.Streams.ReadResp.Types.ReadEvent;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Projections.Core.Tests.Services;

public abstract class SpecificationWithEmittedStreamsTrackerAndDeleter<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private GrpcChannel _channel;
	protected MiniNode<TLogFormat, TStreamId> _node;
	protected StreamsClient _client;
	protected IEmittedStreamsTracker _emittedStreamsTracker;
	protected IEmittedStreamsDeleter _emittedStreamsDeleter;
	protected ProjectionNamesBuilder _projectionNamesBuilder;
	protected ClientMessage.ReadStreamEventsForwardCompleted _readCompleted;
	protected IODispatcher _ioDispatcher;
	protected bool _trackEmittedStreams = true;
	protected string _projectionName = "test_projection";
	protected virtual TimeSpan Timeout { get; } = TimeSpan.FromMinutes(1);

	protected abstract Task When();

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
		await _node.Start();
		_channel = GrpcChannel.ForAddress(new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions { HttpClient = _node.HttpClient, DisposeHttpClient = false });
		_client = new StreamsClient(_channel);
		await Given().WithTimeout(Timeout);
		await When().WithTimeout(Timeout);
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		_channel?.Dispose();
		await _node.Shutdown();
		await base.TestFixtureTearDown();
	}

	protected virtual Task Given()
	{
		_ioDispatcher = new IODispatcher(_node.Node.MainQueue, _node.Node.MainQueue, true);
		_node.Node.MainBus.Subscribe<ClientMessage.ReadStreamEventsBackwardCompleted>(_ioDispatcher.BackwardReader);
		_node.Node.MainBus.Subscribe<ClientMessage.NotHandled>(_ioDispatcher.BackwardReader);
		_node.Node.MainBus.Subscribe(_ioDispatcher.ForwardReader);
		_node.Node.MainBus.Subscribe(_ioDispatcher.Writer);
		_node.Node.MainBus.Subscribe(_ioDispatcher.StreamDeleter);
		_node.Node.MainBus.Subscribe(_ioDispatcher.Awaker);
		_node.Node.MainBus.Subscribe<IODispatcherDelayedMessage>(_ioDispatcher);
		_node.Node.MainBus.Subscribe<ClientMessage.NotHandled>(_ioDispatcher);
		_projectionNamesBuilder = ProjectionNamesBuilder.CreateForTest(_projectionName);
		_emittedStreamsTracker = new EmittedStreamsTracker(_ioDispatcher,
			new ProjectionConfig(null, 1000, 1000 * 1000, 100, 500, true, true, false, false,
				_trackEmittedStreams, 10000, 1, null), _projectionNamesBuilder);
		_emittedStreamsDeleter = new EmittedStreamsDeleter(_ioDispatcher,
			_projectionNamesBuilder.GetEmittedStreamsName(),
			_projectionNamesBuilder.GetEmittedStreamsCheckpointName());
		return Task.CompletedTask;
	}

	protected async Task AppendEvent(string stream, string eventType, byte[] data)
	{
		using var call = _client.Append(AdminCallOptions());
		await call.RequestStream.WriteAsync(new AppendReq
		{
			Options = new()
			{
				Any = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) }
			}
		});
		await call.RequestStream.WriteAsync(new AppendReq
		{
			ProposedMessage = new()
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFrom(data),
				CustomMetadata = ByteString.Empty,
				Metadata = {
					{ GrpcMetadata.Type, eventType },
					{ GrpcMetadata.ContentType, GrpcMetadata.ContentTypes.ApplicationJson }
				}
			}
		});
		await call.RequestStream.CompleteAsync();
		await call.ResponseAsync;
	}

	protected async Task<ReadEvent[]> ReadEvents(string stream, int count)
	{
		using var call = _client.Read(new ReadReq
		{
			Options = new()
			{
				Stream = new()
				{
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) },
					Start = new()
				},
				ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
				Count = (ulong)count,
				NoFilter = new(),
				UuidOption = new() { Structured = new() }
			}
		}, AdminCallOptions());
		var events = new List<ReadEvent>();
		while (await call.ResponseStream.MoveNext(default))
			if (call.ResponseStream.Current.Event is { } resolvedEvent)
				events.Add(resolvedEvent);
		return events.ToArray();
	}

	protected async Task<ReadEvent[]> WaitForEvents(string stream, int count)
	{
		var deadline = DateTime.UtcNow + Timeout;
		ReadEvent[] events;
		do
		{
			events = await ReadEvents(stream, count);
			if (events.Length >= count)
				return events;
			await Task.Delay(50);
		} while (DateTime.UtcNow < deadline);

		return events;
	}

	private static CallOptions AdminCallOptions() => new(
		credentials: CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		}));
}
