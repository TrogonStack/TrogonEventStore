using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Transport.Grpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc;

[TestFixture]
public class GrpcServerShutdownInterceptorTests
{
	[TestCase(MethodType.ClientStreaming)]
	[TestCase(MethodType.ServerStreaming)]
	[TestCase(MethodType.DuplexStreaming)]
	public void shutdown_cancellation_is_retryable_for_every_streaming_shape(MethodType methodType)
	{
		using var lifetime = new TestHostLifetime();
		using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
		lifetime.StopApplication();
		var context = new TestServerCallContext(callCancellation.Token);
		var interceptor = new GrpcServerShutdownInterceptor(lifetime);

		var exception = Assert.ThrowsAsync<RpcException>(() => Invoke(interceptor, methodType, context));

		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unavailable));
		Assert.That(
			exception.Trailers.Select(entry => (entry.Key, entry.Value)),
			Does.Contain((
				EventStore.Core.Services.Transport.Grpc.Constants.Exceptions.ExceptionKey,
				EventStore.Core.Services.Transport.Grpc.Constants.Exceptions.ServerShuttingDown)));
	}

	[Test]
	public void client_cancellation_is_not_changed_when_server_is_running()
	{
		using var lifetime = new TestHostLifetime();
		using var callCancellation = new CancellationTokenSource();
		callCancellation.Cancel();
		var context = new TestServerCallContext(callCancellation.Token);
		var interceptor = new GrpcServerShutdownInterceptor(lifetime);

		var exception = Assert.CatchAsync<OperationCanceledException>(() =>
			Invoke(interceptor, MethodType.ServerStreaming, context));

		Assert.That(exception!.CancellationToken, Is.EqualTo(callCancellation.Token));
	}

	[Test]
	public async Task successful_streaming_call_passes_through()
	{
		using var lifetime = new TestHostLifetime();
		var context = new TestServerCallContext(CancellationToken.None);
		var interceptor = new GrpcServerShutdownInterceptor(lifetime);
		var continuationCalled = false;

		await interceptor.ServerStreamingServerHandler(
			"request",
			new TestServerStreamWriter<string>(),
			context,
			(_, _, _) =>
			{
				continuationCalled = true;
				return Task.CompletedTask;
			});

		Assert.That(continuationCalled, Is.True);
	}

	private static async Task Invoke(
		GrpcServerShutdownInterceptor interceptor,
		MethodType methodType,
		ServerCallContext context)
	{
		switch (methodType)
		{
			case MethodType.ClientStreaming:
				await interceptor.ClientStreamingServerHandler(
					new TestAsyncStreamReader<string>(),
					context,
					(_, callContext) => Task.FromCanceled<string>(callContext.CancellationToken));
				break;
			case MethodType.ServerStreaming:
				await interceptor.ServerStreamingServerHandler(
					"request",
					new TestServerStreamWriter<string>(),
					context,
					(_, _, callContext) => Task.FromCanceled(callContext.CancellationToken));
				break;
			case MethodType.DuplexStreaming:
				await interceptor.DuplexStreamingServerHandler(
					new TestAsyncStreamReader<string>(),
					new TestServerStreamWriter<string>(),
					context,
					(_, _, callContext) => Task.FromCanceled(callContext.CancellationToken));
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(methodType), methodType, null);
		}
	}

	private sealed class TestAsyncStreamReader<T> : IAsyncStreamReader<T>
	{
		public T Current => default!;
		public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
	}

	private sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
	{
		public WriteOptions WriteOptions { get; set; }
		public Task WriteAsync(T message) => Task.CompletedTask;
	}

	private sealed class TestServerCallContext(CancellationToken cancellationToken) : ServerCallContext
	{
		protected override string MethodCore => "/service/method";
		protected override string HostCore => "host";
		protected override string PeerCore => "peer";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => cancellationToken;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions WriteOptionsCore { get; set; }
		protected override AuthContext AuthContextCore { get; } =
			new(string.Empty, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } =
			new Dictionary<object, object>();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
			throw new NotSupportedException();
	}

	private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
	{
		private readonly CancellationTokenSource _stopping = new();

		public CancellationToken ApplicationStarted => CancellationToken.None;
		public CancellationToken ApplicationStopping => _stopping.Token;
		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication() => _stopping.Cancel();
		public void Dispose() => _stopping.Dispose();
	}
}
