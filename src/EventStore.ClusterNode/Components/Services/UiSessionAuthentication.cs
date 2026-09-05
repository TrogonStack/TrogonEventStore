using System;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.Core.Authentication;
using EventStore.Core.Services.Transport.Http;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventStore.ClusterNode.Components.Services;

public static class UiSessionAuthentication
{
	public const string Scheme = "EventStoreUiSession";
	public const string CookieName = "__Host-eventstore-ui-session";
	public const string SessionIdClaim = "eventstore:ui-session-id";
	internal const string OAuthTokenProperty = "ui.oauth-token";
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

	public static IServiceCollection AddUiSessionAuthentication(this IServiceCollection services)
	{
		services.AddSingleton<UiSessionTicketStore>();
		services.AddSingleton<IUiSessionAuthenticator, UiSessionAuthenticator>();
		services.AddScoped<UiSessionEvents>();
		services.AddAuthentication().AddCookie(Scheme, options =>
		{
			options.Cookie.Name = CookieName;
			options.Cookie.Path = "/";
			options.Cookie.HttpOnly = true;
			options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
			options.Cookie.SameSite = SameSiteMode.Lax;
			options.ExpireTimeSpan = Lifetime;
			options.SlidingExpiration = false;
			options.EventsType = typeof(UiSessionEvents);
		});
		return services;
	}

	public static async Task SignInAsync(HttpContext context, ClaimsPrincipal principal, string oauthToken = null)
	{
		if (!context.Request.IsHttps)
			throw new InvalidOperationException("Browser sign-in requires HTTPS.");
		if (principal?.Identity?.IsAuthenticated != true)
			throw new InvalidOperationException("An authenticated identity is required.");

		await context.SignOutAsync(Scheme);
		var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme);
		var issued = (options.TimeProvider ?? TimeProvider.System).GetUtcNow();
		var properties = new AuthenticationProperties
		{
			IsPersistent = false,
			AllowRefresh = false,
			IssuedUtc = issued,
			ExpiresUtc = issued.Add(Lifetime)
		};
		if (oauthToken is not null)
			properties.Items[OAuthTokenProperty] = oauthToken;
		var store = context.RequestServices.GetRequiredService<UiSessionTicketStore>();
		var id = await store.StoreAsync(new AuthenticationTicket(principal, properties, Scheme));
		UiCredentialCookie.DeleteLegacyCookies(context.Response);
		try
		{
			await context.SignInAsync(Scheme,
				new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionIdClaim, id)], Scheme)),
				new AuthenticationProperties
				{
					IsPersistent = false,
					AllowRefresh = false,
					IssuedUtc = issued,
					ExpiresUtc = issued.Add(Lifetime)
				});
		}
		catch
		{
			await store.RemoveAsync(id);
			throw;
		}
	}
}

public sealed class UiSessionAuthenticator : IUiSessionAuthenticator
{
	public async Task<bool> ValidateRequestAsync(HttpContext context)
	{
		if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ||
			HttpMethods.IsOptions(context.Request.Method))
			return true;
		try
		{
			await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
			return true;
		}
		catch (AntiforgeryValidationException)
		{
			return false;
		}
	}

	public async Task<ClaimsPrincipal> AuthenticateAsync(HttpContext context)
	{
		if (!context.Request.IsHttps)
			return null;
		var result = await context.AuthenticateAsync(UiSessionAuthentication.Scheme);
		return result.Succeeded ? result.Principal : null;
	}
}

public sealed class UiSessionEvents(IAuthenticationProvider authenticationProvider, UiSessionTicketStore store) : CookieAuthenticationEvents
{
	public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
	{
		var id = context.Principal?.FindFirst(UiSessionAuthentication.SessionIdClaim)?.Value;
		var ticket = string.IsNullOrEmpty(id) ? null : await store.RetrieveAsync(id);
		ClaimsPrincipal principal = null;
		if (ticket is not null && ticket.Properties.Items.TryGetValue(UiSessionAuthentication.OAuthTokenProperty, out var token))
		{
			var request = new HttpAuthenticationRequest(context.HttpContext, token);
			authenticationProvider.Authenticate(request);
			try
			{
				var (status, authenticated) = await request.AuthenticateAsync().WaitAsync(
					TimeSpan.FromSeconds(5), context.HttpContext.RequestAborted);
				if (status == HttpAuthenticationRequestStatus.Authenticated)
					principal = authenticated;
			}
			catch (TimeoutException)
			{
				principal = null;
			}
		}
		else if (ticket is not null && authenticationProvider is ISessionAuthenticationProvider sessions)
		{
			principal = await sessions.ValidateSessionAsync(ticket.Principal, context.HttpContext.RequestAborted);
		}

		if (principal?.Identity?.IsAuthenticated != true)
		{
			context.RejectPrincipal();
			await context.HttpContext.SignOutAsync(UiSessionAuthentication.Scheme);
			return;
		}
		context.ReplacePrincipal(principal);
	}

	public override async Task SigningOut(CookieSigningOutContext context)
	{
		var cookie = context.Options.CookieManager.GetRequestCookie(context.HttpContext, context.Options.Cookie.Name);
		var ticket = string.IsNullOrEmpty(cookie) ? null : context.Options.TicketDataFormat.Unprotect(cookie);
		var id = ticket?.Principal.FindFirst(UiSessionAuthentication.SessionIdClaim)?.Value;
		if (!string.IsNullOrEmpty(id))
			await store.RemoveAsync(id);
	}

	public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
	{
		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return Task.CompletedTask;
	}

	public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
	{
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
		return Task.CompletedTask;
	}
}
