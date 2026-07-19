using System;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Hosting;

namespace EventStore.Core.Services.Transport.Grpc;

public sealed class GrpcServerShutdownInterceptor(IHostApplicationLifetime applicationLifetime) : Interceptor
{
	public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
		IAsyncStreamReader<TRequest> requestStream,
		ServerCallContext context,
		ClientStreamingServerMethod<TRequest, TResponse> continuation) =>
		TranslateShutdownAsync(context, () => continuation(requestStream, context));

	public override Task ServerStreamingServerHandler<TRequest, TResponse>(
		TRequest request,
		IServerStreamWriter<TResponse> responseStream,
		ServerCallContext context,
		ServerStreamingServerMethod<TRequest, TResponse> continuation) =>
		TranslateShutdownAsync(context, () => continuation(request, responseStream, context));

	public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
		IAsyncStreamReader<TRequest> requestStream,
		IServerStreamWriter<TResponse> responseStream,
		ServerCallContext context,
		DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
		TranslateShutdownAsync(context, () => continuation(requestStream, responseStream, context));

	private async Task TranslateShutdownAsync(ServerCallContext context, Func<Task> continuation)
	{
		try
		{
			await continuation();
		}
		catch (OperationCanceledException exception) when (IsShutdownCancellation(exception, context))
		{
			throw RpcExceptions.ServerShuttingDown();
		}
	}

	private async Task<TResponse> TranslateShutdownAsync<TResponse>(
		ServerCallContext context,
		Func<Task<TResponse>> continuation)
	{
		try
		{
			return await continuation();
		}
		catch (OperationCanceledException exception) when (IsShutdownCancellation(exception, context))
		{
			throw RpcExceptions.ServerShuttingDown();
		}
	}

	private bool IsShutdownCancellation(OperationCanceledException exception, ServerCallContext context) =>
		applicationLifetime.ApplicationStopping.IsCancellationRequested &&
		context.CancellationToken.IsCancellationRequested &&
		exception.CancellationToken == context.CancellationToken;
}
