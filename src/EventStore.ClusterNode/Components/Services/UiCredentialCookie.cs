using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public static class UiCredentialCookie
{
	public const string BasicCookieName = "es-creds";
	public const string OAuthCookieName = "oauth_token";

	public static void DeleteLegacyCookies(HttpResponse response)
	{
		var options = new CookieOptions { HttpOnly = true, Path = "/", SameSite = SameSiteMode.Lax, Secure = response.HttpContext.Request.IsHttps };
		response.Cookies.Delete(BasicCookieName, options);
		response.Cookies.Delete(OAuthCookieName, options);
	}
}
