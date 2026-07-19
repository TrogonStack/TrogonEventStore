using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Transport.Grpc;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc;

[TestFixture]
public class GrpcStreamLifetimeMiddlewareTests
{
	[TestCase(MethodType.ClientStreaming)]
	[TestCase(MethodType.ServerStreaming)]
	[TestCase(MethodType.DuplexStreaming)]
	public async Task streaming_calls_observe_host_shutdown(MethodType methodType)
	{
		using var lifetime = new TestHostLifetime();
		var context = CreateContext(methodType);
		var observedCancellation = false;
		var middleware = new GrpcStreamLifetimeMiddleware(nextContext =>
		{
			lifetime.StopApplication();
			observedCancellation = nextContext.RequestAborted.IsCancellationRequested;
			return Task.CompletedTask;
		}, lifetime);

		await middleware.InvokeAsync(context);

		Assert.That(observedCancellation, Is.True);
	}

	[Test]
	public async Task unary_calls_are_not_cancelled_by_host_shutdown()
	{
		using var lifetime = new TestHostLifetime();
		var context = CreateContext(MethodType.Unary);
		var observedCancellation = true;
		var middleware = new GrpcStreamLifetimeMiddleware(nextContext =>
		{
			lifetime.StopApplication();
			observedCancellation = nextContext.RequestAborted.IsCancellationRequested;
			return Task.CompletedTask;
		}, lifetime);

		await middleware.InvokeAsync(context);

		Assert.That(observedCancellation, Is.False);
	}

	[Test]
	public async Task streaming_calls_keep_client_abort_linked()
	{
		using var lifetime = new TestHostLifetime();
		using var clientAbort = new CancellationTokenSource();
		var context = CreateContext(MethodType.ServerStreaming);
		context.RequestAborted = clientAbort.Token;
		var observedCancellation = false;
		var middleware = new GrpcStreamLifetimeMiddleware(nextContext =>
		{
			clientAbort.Cancel();
			observedCancellation = nextContext.RequestAborted.IsCancellationRequested;
			return Task.CompletedTask;
		}, lifetime);

		await middleware.InvokeAsync(context);

		Assert.That(observedCancellation, Is.True);
	}

	[Test]
	public async Task non_grpc_requests_are_not_linked_to_host_shutdown()
	{
		using var lifetime = new TestHostLifetime();
		var context = new DefaultHttpContext();
		var observedCancellation = true;
		var middleware = new GrpcStreamLifetimeMiddleware(nextContext =>
		{
			lifetime.StopApplication();
			observedCancellation = nextContext.RequestAborted.IsCancellationRequested;
			return Task.CompletedTask;
		}, lifetime);

		await middleware.InvokeAsync(context);

		Assert.That(observedCancellation, Is.False);
	}

	[Test]
	public async Task original_request_token_is_restored_after_stream_completes()
	{
		using var lifetime = new TestHostLifetime();
		using var clientAbort = new CancellationTokenSource();
		var context = CreateContext(MethodType.ServerStreaming);
		context.RequestAborted = clientAbort.Token;
		var middleware = new GrpcStreamLifetimeMiddleware(_ => Task.CompletedTask, lifetime);

		await middleware.InvokeAsync(context);

		Assert.That(context.RequestAborted, Is.EqualTo(clientAbort.Token));
	}

	private static DefaultHttpContext CreateContext(MethodType methodType)
	{
		var context = new DefaultHttpContext();
		context.SetEndpoint(new Endpoint(
			_ => Task.CompletedTask,
			new EndpointMetadataCollection(new GrpcMethodMetadata(typeof(object), new TestMethod(methodType))),
			"test"));
		return context;
	}

	private sealed class TestMethod(MethodType type) : IMethod
	{
		public MethodType Type => type;
		public string ServiceName => "service";
		public string Name => "method";
		public string FullName => "/service/method";
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
