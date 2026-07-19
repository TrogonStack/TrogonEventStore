using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace EventStore.Core.Services.Transport.Grpc;

public sealed class GrpcStreamLifetimeMiddleware(
	RequestDelegate next,
	IHostApplicationLifetime applicationLifetime)
{
	public Task InvokeAsync(HttpContext context)
	{
		var methodType = context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>()?.Method.Type;
		return methodType is MethodType.ClientStreaming or MethodType.ServerStreaming or MethodType.DuplexStreaming
			? InvokeStreamingAsync(context)
			: next(context);
	}

	private async Task InvokeStreamingAsync(HttpContext context)
	{
		var requestAborted = context.RequestAborted;
		using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(
			requestAborted,
			applicationLifetime.ApplicationStopping);
		context.RequestAborted = streamLifetime.Token;

		try
		{
			await next(context);
		}
		finally
		{
			context.RequestAborted = requestAborted;
		}
	}
}
