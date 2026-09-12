using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Transforms.BitFlip;
using EventStore.Core.Tests.Transforms.ByteDup;
using EventStore.Core.Tests.Transforms.WithHeader;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.Transforms.Identity;
using EventStore.Plugins.Transforms;
using Google.Protobuf;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Core.Tests.Transforms;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class TransformTests<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private const int NumEvents = 1000;
	private const int BatchSize = 50;
	private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

	[TestCase("identity")]
	[TestCase("bitflip")]
	[TestCase("bytedup")]
	[TestCase("withheader")]
	[Timeout(600000)]
	public async Task transform_works(string transform)
	{
		MiniNode<TLogFormat, TStreamId> node = null;
		GrpcChannel connection = null;
		var dbPath = Path.Combine(PathName, $"node-{Guid.NewGuid()}");
		try
		{
			(node, connection) = await CreateNode(dbPath, transform);

			var writtenIds = await WriteEvents(connection);
			await VerifyEvents(connection, writtenIds);

			await ShutdownNode(node, connection, keepDb: true);
			(node, connection) = await CreateNode(dbPath, transform);

			// then verify the chunk checksums
			await VerifyChecksums(node);

			// and verify the events again
			await VerifyEvents(connection, writtenIds);
		}
		finally
		{
			await ShutdownNode(node, connection);
		}
	}

	private async ValueTask VerifyChecksums(MiniNode<TLogFormat, TStreamId> node, CancellationToken token = default)
	{
		var completedChunks = new List<TFChunk>();
		for (var i = 0; ; i++)
		{
			try
			{
				var chunk = node.Db.Manager.GetChunk(i);
				if (chunk.IsReadOnly)
				{
					completedChunks.Add(chunk);
				}
			}
			catch (ArgumentOutOfRangeException)
			{
				break;
			}
		}

		foreach (var chunk in completedChunks)
		{
			await chunk.VerifyFileHash(token);
		}
	}

	private async Task<(MiniNode<TLogFormat, TStreamId>, GrpcChannel)> CreateNode(string dbPath, string transform)
	{
		IDbTransform dbTransform = transform switch
		{
			"identity" => new IdentityDbTransform(),
			"bitflip" => new BitFlipDbTransform(),
			"bytedup" => new ByteDupDbTransform(),
			"withheader" => new WithHeaderDbTransform(),
			_ => throw new ArgumentOutOfRangeException()
		};

		var node = new MiniNode<TLogFormat, TStreamId>(
			pathname: PathName,
			dbPath: dbPath,
			chunkSize: 10_000,
			cachedChunkSize: (10_000 + ChunkHeader.Size + ChunkFooter.Size) * 2,
			transform: dbTransform.Name,
			newTransforms: [dbTransform]);
		await node.Start(StartupTimeout);

		var connection = BuildConnection(node);

		return (node, connection);
	}

	private static async Task ShutdownNode(
		MiniNode<TLogFormat, TStreamId> node,
		GrpcChannel connection,
		bool keepDb = false)
	{
		if (node is not null)
		{
			await node.Shutdown(keepDb);
		}

		connection?.Dispose();
	}

	private static GrpcChannel BuildConnection(MiniNode<TLogFormat, TStreamId> node)
	{
		return GrpcChannel.ForAddress(new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions { HttpClient = node.HttpClient, DisposeHttpClient = false });
	}

	private static async Task<Guid[]> WriteEvents(GrpcChannel connection)
	{
		var writtenIds = new List<Guid>();
		var client = new StreamsClient(connection);

		for (var i = 0; i < NumEvents / BatchSize; i++)
		{
			var events = CreateEventBatch(BatchSize);
			using var call = client.Append();
			await call.RequestStream.WriteAsync(new AppendReq
			{
				Options = new()
				{
					Any = new(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8("test") }
				}
			});
			foreach (var @event in events)
				await call.RequestStream.WriteAsync(new AppendReq { ProposedMessage = @event });
			await call.RequestStream.CompleteAsync();
			await call.ResponseAsync;
			writtenIds.AddRange(events.Select(x => Uuid.FromDto(x.Id).ToGuid()));
		}

		return writtenIds.ToArray();
	}

	private static AppendReq.Types.ProposedMessage[] CreateEventBatch(int numEvents)
	{
		var events = new AppendReq.Types.ProposedMessage[numEvents];

		for (var i = 0; i < numEvents; i++)
		{
			events[i] = new()
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFromUtf8("{ \"foo\":\"bar\" }"),
				CustomMetadata = ByteString.Empty,
				Metadata = {
					{ GrpcMetadata.Type, "testEvent" },
					{ GrpcMetadata.ContentType, GrpcMetadata.ContentTypes.ApplicationJson }
				}
			};
		}

		return events;
	}

	private static async Task VerifyEvents(GrpcChannel connection, Guid[] writtenIds)
	{
		var client = new StreamsClient(connection);
		using var call = client.Read(new ReadReq
		{
			Options = new()
			{
				Stream = new()
				{
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8("test") },
					Start = new()
				},
				ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
				Count = ulong.MaxValue,
				NoFilter = new(),
				UuidOption = new() { Structured = new() }
			}
		});
		var readIds = new List<Guid>();
		while (await call.ResponseStream.MoveNext(default))
			if (call.ResponseStream.Current.Event is { } resolvedEvent)
				readIds.Add(Uuid.FromDto(resolvedEvent.Event.Id).ToGuid());

		Assert.AreEqual(NumEvents, readIds.Count);
		Assert.True(writtenIds.SequenceEqual(readIds));
	}
}
