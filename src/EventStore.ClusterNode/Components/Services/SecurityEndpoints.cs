using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class SecurityEndpoints {
	public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app) {
		app.MapPost("/ui/security/migrate-credentials", async (HttpContext context, SecurityBrowserService security) => {
			SecurityCredentialMigrationRequest request;
			try {
				request = await context.Request.ReadFromJsonAsync<SecurityCredentialMigrationRequest>(
					cancellationToken: context.RequestAborted);
			} catch (Exception ex) when (ex is BadHttpRequestException or JsonException) {
				return Results.BadRequest();
			}

			if (request is null || string.IsNullOrWhiteSpace(request.Credentials))
				return Results.BadRequest();

			if (!UiCredentialCookie.TryParseBasicCredentials(request.Credentials, out var credentials))
				return Results.BadRequest();

			SecurityCommandResult validation;
			try {
				validation = await security.Validate(credentials.Username, credentials.Password);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception) {
				return Results.Unauthorized();
			}

			if (!validation.Success)
				return Results.Unauthorized();

			UiCredentialCookie.AppendBasic(context.Response, credentials);
			return Results.NoContent();
		});

		return app;
	}
}

internal sealed record SecurityCredentialMigrationRequest(string Credentials);
