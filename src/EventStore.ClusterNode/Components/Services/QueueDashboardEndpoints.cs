using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class QueueDashboardEndpoints {
	public static IEndpointRouteBuilder MapQueueDashboardEndpoints(this IEndpointRouteBuilder app) {
		app.MapGet("/ui/queue-dashboard/payload", async (
			HttpContext context,
			QueueDashboardService dashboard) => {
			var page = await dashboard.Read(context.RequestAborted);
			context.Response.Headers.CacheControl = "no-store";
			return Results.Text(page.ClientPayloadJson, "application/json", Encoding.UTF8);
		}).RequireAuthorization();

		return app;
	}
}
