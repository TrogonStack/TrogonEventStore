using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Helpers;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using Empty = EventStore.Client.Empty;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using RecordedEvent = EventStore.Client.Streams.ReadResp.Types.ReadEvent.Types.RecordedEvent;

namespace EventStore.Core.Tests.Services.Transport.Enumerators;

public partial class EnumeratorTests
{
	[TestFixture]
	public class TestFixtureWithMiniNodeConnection : SpecificationWithDirectoryPerTestFixture
	{
		private static readonly CallCredentials AdminCredentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});

		protected MiniNode<LogFormat.V2, string> Node { get; private set; }
		private GrpcChannel Channel { get; set; }
		private Streams.StreamsClient StreamsClient { get; set; }

		[OneTimeSetUp]
		public override async Task TestFixtureSetUp()
		{
			await base.TestFixtureSetUp();
			Node = new MiniNode<LogFormat.V2, string>(PathName);
			await Node.Start();
			await Node.AdminUserCreated;
			Channel = GrpcChannel.ForAddress(new Uri($"https://{Node.HttpEndPoint}"),
				new GrpcChannelOptions
				{
					HttpClient = Node.HttpClient,
					DisposeHttpClient = false,
				});
			StreamsClient = new Streams.StreamsClient(Channel);
		}

		protected async Task AppendToStream(
			string stream,
			IEnumerable<(string EventType, byte[] Data, byte[] Metadata)> events)
		{
			using var call = StreamsClient.Append(GetCallOptions());
			await call.RequestStream.WriteAsync(new AppendReq
			{
				Options = new()
				{
					Any = new Empty(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) }
				}
			});

			foreach (var @event in events)
			{
				await call.RequestStream.WriteAsync(new AppendReq
				{
					ProposedMessage = new()
					{
						Id = Uuid.NewUuid().ToDto(),
						Data = ByteString.CopyFrom(@event.Data),
						CustomMetadata = ByteString.CopyFrom(@event.Metadata),
						Metadata =
						{
							[GrpcMetadata.Type] = @event.EventType,
							[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
						}
					}
				});
			}

			await call.RequestStream.CompleteAsync();
			var response = await call.ResponseAsync;
			Assert.That(response.ResultCase, Is.EqualTo(AppendResp.ResultOneofCase.Success));
		}

		protected Task AppendToStream(string stream, string eventType, string data, string metadata) =>
			AppendToStream(stream,
				[(eventType, Encoding.UTF8.GetBytes(data ?? string.Empty), Encoding.UTF8.GetBytes(metadata ?? string.Empty))]);

		protected async Task<IReadOnlyList<RecordedEvent>> ReadAllEvents()
		{
			using var call = StreamsClient.Read(new ReadReq
			{
				Options = new()
				{
					All = new() { Start = new Empty() },
					ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
					Count = ulong.MaxValue,
					NoFilter = new Empty(),
					UuidOption = new() { Structured = new Empty() }
				}
			}, GetCallOptions());

			return await call.ResponseStream.ReadAllAsync()
				.Where(response => response.Event is not null)
				.Select(response => response.Event.Event)
				.ToArrayAsync();
		}

		protected async Task DeleteStream(string stream, bool hardDelete)
		{
			if (hardDelete)
			{
				using var call = StreamsClient.TombstoneAsync(new TombstoneReq
				{
					Options = new()
					{
						Any = new Empty(),
						StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) }
					}
				}, GetCallOptions());
				await call.ResponseAsync;
				return;
			}

			using var deleteCall = StreamsClient.DeleteAsync(new DeleteReq
			{
				Options = new()
				{
					Any = new Empty(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) }
				}
			}, GetCallOptions());
			await deleteCall.ResponseAsync;
		}

		protected async Task<long> ReadLastStreamRevision(string stream)
		{
			using var call = StreamsClient.Read(new ReadReq
			{
				Options = new()
				{
					Stream = new()
					{
						StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) },
						End = new Empty()
					},
					ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Backwards,
					Count = 1,
					NoFilter = new Empty(),
					UuidOption = new() { Structured = new Empty() }
				}
			}, GetCallOptions());

			var response = await call.ResponseStream.ReadAllAsync()
				.FirstAsync(response => response.Event is not null);
			return checked((long)response.Event.Event.StreamRevision);
		}

		private static CallOptions GetCallOptions() => new(
			credentials: AdminCredentials,
			deadline: DateTime.UtcNow.AddSeconds(30));

		[OneTimeTearDown]
		public override async Task TestFixtureTearDown()
		{
			Channel?.Dispose();
			await Node.Shutdown();
			await base.TestFixtureTearDown();
		}
	}
}
