using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace EventStore.ClusterNode.Components.Services;

public sealed class UiCredentialsMiddleware(RequestDelegate next) {
	public Task InvokeAsync(HttpContext context) {
		if (!context.Request.Headers.ContainsKey(HeaderNames.Authorization) &&
		    UiCredentialCookie.TryReadAuthorization(context.Request, out var scheme, out var value))
			context.Request.Headers.Authorization = $"{scheme} {value}";

		return next(context);
	}
}
