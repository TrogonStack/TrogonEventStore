using System;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Common.Options;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Util;
using EventStore.Projections.Core.Services.Processing;
using Google.Protobuf;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Projections.Core.Tests.Services.grpc_service;

public abstract class SpecificationWithNodeAndProjectionSubsystem<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected MiniNode<TLogFormat, TStreamId> _node;
	private GrpcChannel _channel;
	protected StreamsClient _connection;
	protected TimeSpan _timeout;
	protected string _tag;
	protected virtual TimeSpan StartupTimeout => TimeSpan.FromMinutes(5);
	private Task _systemProjectionsCreated;
	private ProjectionsSubsystem _projectionsSubsystem;


	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_timeout = TimeSpan.FromSeconds(20);
		_tag = "_1";

		_node = CreateNode();
		await _node.Start(StartupTimeout);

		await _systemProjectionsCreated.WithTimeout(_timeout);

		_channel = GrpcChannel.ForAddress(new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions { HttpClient = _node.HttpClient, DisposeHttpClient = false });
		_connection = new StreamsClient(_channel);

		try
		{
			await Given().WithTimeout(_timeout);
		}
		catch (Exception ex)
		{
			throw new Exception("Given Failed", ex);
		}

		try
		{
			await When().WithTimeout(_timeout);
		}
		catch (Exception ex)
		{
			throw new Exception("When Failed", ex);
		}
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		_channel?.Dispose();
		await _node.Shutdown();
		await Task.Delay(1000);

		await base.TestFixtureTearDown();
	}

	public abstract Task Given();
	public abstract Task When();

	protected MiniNode<TLogFormat, TStreamId> CreateNode()
	{
		_projectionsSubsystem = new ProjectionsSubsystem(new ProjectionSubsystemOptions(1, ProjectionType.All, false, TimeSpan.FromMinutes(Opts.ProjectionsQueryExpiryDefault), Opts.FaultOutOfOrderProjectionsDefault, 500, 250));
		_systemProjectionsCreated = SystemProjections.Created(_projectionsSubsystem.LeaderInputBus);
		return new MiniNode<TLogFormat, TStreamId>(
			PathName,
			subsystems: [_projectionsSubsystem]);
	}

	protected async Task PostEvent(string stream, string eventType, string data)
	{
		using var call = _connection.Append();
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
				Data = ByteString.CopyFromUtf8(data),
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

	protected string CreateStandardQuery(string stream)
	{
		return @"fromStream(""" + stream + @""")
                .when({
                    ""$any"":function(s,e) {
                        s.count = 1;
                        return s;
                    }
            });";
	}

	protected string CreateEmittingQuery(string stream, string emittingStream)
	{
		return @"fromStream(""" + stream + @""")
                .when({
                    ""$any"":function(s,e) {
                        emit(""" + emittingStream + @""", ""emittedEvent"", e);
                    }
                });";
	}

}
