using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class UiCredentialsMiddleware(RequestDelegate next)
{
	public Task InvokeAsync(HttpContext context)
	{
		if (context.Request.Cookies.ContainsKey(UiCredentialCookie.BasicCookieName) ||
			context.Request.Cookies.ContainsKey(UiCredentialCookie.OAuthCookieName))
		{
			UiCredentialCookie.DeleteLegacyCookies(context.Response);
		}

		return next(context);
	}
}
