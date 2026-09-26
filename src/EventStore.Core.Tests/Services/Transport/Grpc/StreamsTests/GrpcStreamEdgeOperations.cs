using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Helpers;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using Streams = EventStore.Client.Streams.Streams;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

internal sealed class GrpcStreamEdgeOperations : IDisposable
{
	private readonly GrpcChannel _channel;
	private readonly Streams.StreamsClient _client;
	private readonly CallCredentials _credentials;
	private CallOptions CallOptions => new(credentials: _credentials,
		deadline: DateTime.UtcNow.AddSeconds(20));

	public GrpcStreamEdgeOperations(MiniNode<LogFormat.V2, string> node)
	{
		_channel = GrpcChannel.ForAddress(new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions { HttpClient = node.HttpClient, DisposeHttpClient = false });
		_client = new Streams.StreamsClient(_channel);
		_credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization", "Basic " + Convert.ToBase64String(
				Encoding.ASCII.GetBytes("admin:changeit")));
			return Task.CompletedTask;
		});
	}

	public GrpcStreamEdgeOperations(GrpcChannel channel)
	{
		_client = new Streams.StreamsClient(channel);
		_credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization", "Basic " + Convert.ToBase64String(
				Encoding.ASCII.GetBytes("admin:changeit")));
			return Task.CompletedTask;
		});
	}

	public async Task<BatchAppendResp> Append(
		string streamName,
		int count = 1,
		ulong? expectedRevision = null,
		bool noStream = false,
		string data = "event",
		string eventType = "event")
	{
		using var call = _client.BatchAppend(CallOptions);
		var options = new BatchAppendReq.Types.Options
		{
			StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
		};
		if (expectedRevision.HasValue)
			options.StreamPosition = expectedRevision.Value;
		else if (noStream)
			options.NoStream = new();
		else
			options.Any = new();

		var request = new BatchAppendReq
		{
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			Options = options
		};
		for (var index = 0; index < count; index++)
		{
			request.ProposedMessages.Add(new BatchAppendReq.Types.ProposedMessage
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFromUtf8(data),
				Metadata =
				{
					[GrpcMetadata.Type] = eventType,
					[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
				}
			});
		}
		await call.RequestStream.WriteAsync(request);
		await call.RequestStream.CompleteAsync();
		Assert.True(await call.ResponseStream.MoveNext());
		return call.ResponseStream.Current;
	}

	public async Task<AppendResp> AppendSingle(string streamName, ulong? expectedRevision = null)
	{
		using var call = _client.Append(CallOptions);
		var options = new AppendReq.Types.Options
		{
			StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
		};
		if (expectedRevision.HasValue)
			options.Revision = expectedRevision.Value;
		else
			options.Any = new();
		await call.RequestStream.WriteAsync(new AppendReq
		{
			Options = options
		});
		await call.RequestStream.WriteAsync(new AppendReq
		{
			ProposedMessage = new()
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFromUtf8("event"),
				Metadata =
				{
					[GrpcMetadata.Type] = "event",
					[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
				}
			}
		});
		await call.RequestStream.CompleteAsync();
		return await call.ResponseAsync;
	}

	public async Task<ReadResp[]> Read(
		string streamName,
		ulong revision,
		ulong count,
		ReadReq.Types.Options.Types.ReadDirection direction =
			ReadReq.Types.Options.Types.ReadDirection.Forwards)
	{
		using var call = _client.Read(new ReadReq
		{
			Options = new()
			{
				Stream = new()
				{
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) },
					Revision = revision
				},
				Count = count,
				ReadDirection = direction,
				NoFilter = new(),
				UuidOption = new() { Structured = new() }
			}
		}, CallOptions);
		return await call.ResponseStream.ReadAllAsync().ToArrayAsync();
	}

	public void Dispose() => _channel?.Dispose();
}
