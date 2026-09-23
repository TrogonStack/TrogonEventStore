using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using GrpcStreams = EventStore.Client.Streams.Streams;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture]
public class ForwardedAuthenticationTests
{
	[Test]
	public void append_reports_forwarded_authentication_failure()
	{
		var service = CreateService(new AuthenticationFailurePublisher());
		var requests = new EnumerableStreamReader<AppendReq>([
			new AppendReq {
				Options = new AppendReq.Types.Options {
					NoStream = new(),
					StreamIdentifier = "forwarded-auth-append"
				}
			},
			new AppendReq {
				ProposedMessage = new AppendReq.Types.ProposedMessage {
					Id = Uuid.NewUuid().ToDto(),
					Metadata = {
						[GrpcMetadata.Type] = "test",
						[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
					},
					Data = ByteString.CopyFromUtf8("{}")
				}
			}
		]);

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Append(requests, new TestServerCallContext()));

		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unauthenticated));
		Assert.That(exception.Status.Detail, Does.Contain("forwarding denied"));
	}

	[Test]
	public void delete_reports_forwarded_authentication_failure()
	{
		var service = CreateService(new AuthenticationFailurePublisher());
		var request = new DeleteReq {
			Options = new DeleteReq.Types.Options {
				NoStream = new(),
				StreamIdentifier = "forwarded-auth-delete"
			}
		};

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Delete(request, new TestServerCallContext()));

		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unauthenticated));
		Assert.That(exception.Status.Detail, Does.Contain("forwarding denied"));
	}

	private static GrpcStreams.StreamsBase CreateService(IPublisher publisher)
	{
		var type = typeof(GrpcTrackers).Assembly.GetType(
			"EventStore.Core.Services.Transport.Grpc.Streams`1", throwOnError: true)!
			.MakeGenericType(typeof(string));
		return (GrpcStreams.StreamsBase)Activator.CreateInstance(type,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: [publisher, 1024, TimeSpan.FromSeconds(1), null, new GrpcTrackers(), new PassthroughAuthorizationProvider()],
			culture: null)!;
	}

	private sealed class AuthenticationFailurePublisher : IPublisher
	{
		public void Publish(Message message)
		{
			if (message is not ClientMessage.WriteRequestMessage request)
				throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");
			request.Envelope.ReplyWith(new ClientMessage.NotAuthenticated(request.CorrelationId, "forwarding denied"));
		}
	}

	private sealed class EnumerableStreamReader<T>(IEnumerable<T> values) : IAsyncStreamReader<T>
	{
		private readonly IEnumerator<T> _values = values.GetEnumerator();
		public T Current { get; private set; } = default!;

		public Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			if (!_values.MoveNext())
				return Task.FromResult(false);
			Current = _values.Current;
			return Task.FromResult(true);
		}
	}

	private sealed class TestServerCallContext : ServerCallContext
	{
		public TestServerCallContext()
		{
			UserStateCore["__HttpContext"] = new DefaultHttpContext {
				User = new ClaimsPrincipal(new ClaimsIdentity())
			};
		}

		protected override string MethodCore => "/event_store.client.streams.Streams/Append";
		protected override string HostCore => "localhost";
		protected override string PeerCore => "ipv4:127.0.0.1:2113";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => CancellationToken.None;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions WriteOptionsCore { get; set; }
		protected override AuthContext AuthContextCore { get; } =
			new(string.Empty, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
			throw new NotSupportedException();
	}
}
