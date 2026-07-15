using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core.Authentication;

public class CompositeAuthenticationProvider(IReadOnlyList<IAuthenticationProvider> providers)
	: AuthenticationProviderBase(name: "methods", diagnosticsName: "CompositeAuthentication")
{
	public override Task Initialize() =>
		Task.WhenAll(providers.Select(provider => provider.Initialize()));

	public override void Authenticate(AuthenticationRequest authenticationRequest)
	{
		var scheme = SelectScheme(authenticationRequest);
		var provider = providers.FirstOrDefault(candidate =>
			candidate.GetSupportedAuthenticationSchemes()?.Contains(scheme, StringComparer.OrdinalIgnoreCase) == true);

		if (provider is null)
		{
			authenticationRequest.Unauthorized();
			return;
		}

		provider.Authenticate(authenticationRequest);
	}

	public override IEnumerable<KeyValuePair<string, string>> GetPublicProperties() =>
		providers.SelectMany(provider => provider.GetPublicProperties() ?? []);

	public override void ConfigureEndpoints(IEndpointRouteBuilder endpointRouteBuilder)
	{
		foreach (var provider in providers)
		{
			provider.ConfigureEndpoints(endpointRouteBuilder);
		}
	}

	public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
	{
		foreach (var provider in providers)
		{
			provider.ConfigureServices(services, configuration);
		}
	}

	public override void ConfigureApplication(IApplicationBuilder app, IConfiguration configuration)
	{
		foreach (var provider in providers)
		{
			provider.ConfigureApplication(app, configuration);
		}
	}

	public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() =>
		providers
			.SelectMany(provider => provider.GetSupportedAuthenticationSchemes() ?? [])
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static string SelectScheme(AuthenticationRequest authenticationRequest)
	{
		if (!string.IsNullOrWhiteSpace(authenticationRequest.GetToken("jwt")))
		{
			return "Bearer";
		}

		return authenticationRequest.HasValidClientCertificate ? "UserCertificate" : "Basic";
	}
}
