using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class ClusterStatusEndpoints {
	public static IEndpointRouteBuilder MapClusterStatusEndpoints(this IEndpointRouteBuilder app) {
		app.MapGet("/ui/cluster/membership", async (
			HttpContext context,
			ClusterStatusService clusterStatus) => {
			try {
				var status = await clusterStatus.Read(context.User, context.RequestAborted);
				return Results.Json(status, ClusterStatusJson.Options);
			} catch (UnauthorizedAccessException) {
				return Results.StatusCode(StatusCodes.Status403Forbidden);
			} catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
				throw;
			} catch (TimeoutException) {
				return Results.Problem("Timed out reading cluster membership.", statusCode: StatusCodes.Status504GatewayTimeout);
			} catch (Exception ex) {
				return Results.Problem(
					$"Unable to read cluster membership: {UiMessages.Friendly(ex)}",
					statusCode: StatusCodes.Status503ServiceUnavailable);
			}
		});

		return app;
	}
}
