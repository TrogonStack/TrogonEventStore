using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class SecurityEndpoints {
	public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app) {
		app.MapPost("/ui/security/migrate-credentials", async (HttpContext context) => {
			SecurityCredentialMigrationRequest request;
			try {
				request = await context.Request.ReadFromJsonAsync<SecurityCredentialMigrationRequest>(
					cancellationToken: context.RequestAborted);
			} catch (Exception ex) when (ex is BadHttpRequestException or JsonException) {
				return Results.BadRequest();
			}

			if (request is null || string.IsNullOrWhiteSpace(request.Credentials))
				return Results.BadRequest();

			return UiCredentialCookie.TryAppendBasicValue(context.Response, request.Credentials)
				? Results.NoContent()
				: Results.BadRequest();
		}).AllowAnonymous();

		return app;
	}
}

internal sealed record SecurityCredentialMigrationRequest(string Credentials);
