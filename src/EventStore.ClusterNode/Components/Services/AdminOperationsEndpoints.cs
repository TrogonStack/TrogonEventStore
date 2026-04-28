using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class AdminOperationsEndpoints {
	public static IEndpointRouteBuilder MapAdminOperationsEndpoints(this IEndpointRouteBuilder app) {
		app.MapPost("/ui/operations/scavenge/start", async (
			HttpContext context,
			AdminOperationsService operations) => {
			var (request, error) = await ReadBody(context, new ScavengeStartRequest());
			return await ToJson(async () => error is not null
				? AdminCommandResult.Failed(error, StatusCodes.Status400BadRequest)
				: await operations.StartScavenge(request, context.RequestAborted));
		});

		app.MapPost("/ui/operations/scavenge/stop", async (
			HttpContext context,
			AdminOperationsService operations) => {
			var (request, error) = await ReadBody(context, new ScavengeStopRequest(""));
			return await ToJson(async () => error is not null
				? AdminCommandResult.Failed(error, StatusCodes.Status400BadRequest)
				: await operations.StopScavenge(request, context.RequestAborted));
		});

		app.MapPost("/ui/operations/reload-config", async (
			HttpContext context,
			AdminOperationsService operations) =>
			await ToJson(() => operations.ReloadConfig(context.RequestAborted)));

		app.MapPost("/ui/operations/merge-indexes", async (
			HttpContext context,
			AdminOperationsService operations) =>
			await ToJson(() => operations.MergeIndexes(context.RequestAborted)));

		app.MapPost("/ui/operations/resign", async (
			HttpContext context,
			AdminOperationsService operations) =>
			await ToJson(() => operations.ResignNode(context.RequestAborted)));

		app.MapPost("/ui/operations/set-priority", async (
			HttpContext context,
			AdminOperationsService operations) => {
			var (request, error) = await ReadBody(context, new SetNodePriorityRequest(0));
			return await ToJson(async () => error is not null
				? AdminCommandResult.Failed(error, StatusCodes.Status400BadRequest)
				: await operations.SetNodePriority(request, context.RequestAborted));
		});

		app.MapPost("/ui/operations/shutdown", async (
			HttpContext context,
			AdminOperationsService operations) =>
			await ToJson(() => operations.Shutdown(context.RequestAborted)));

		return app;
	}

	private static async Task<(T Request, string Error)> ReadBody<T>(HttpContext context, T fallback) {
		try {
			return (await context.Request.ReadFromJsonAsync<T>(cancellationToken: context.RequestAborted) ?? fallback, null);
		} catch (Exception ex) when (ex is BadHttpRequestException or System.Text.Json.JsonException) {
			return (fallback, "The command payload was not valid JSON.");
		}
	}

	private static IResult ToJson(AdminCommandResult result) =>
		Results.Json(result, statusCode: result.StatusCode);

	private static async Task<IResult> ToJson(Func<Task<AdminCommandResult>> command) {
		try {
			return ToJson(await command());
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ToJson(AdminCommandResult.Failed(FriendlyMessage(ex)));
		}
	}

	private static string FriendlyMessage(Exception ex) =>
		string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}
