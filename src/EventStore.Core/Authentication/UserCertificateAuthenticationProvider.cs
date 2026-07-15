using System.Collections.Generic;
using System.Threading.Tasks;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core.Authentication;

public class UserCertificateAuthenticationProvider(IAuthenticationProvider inner)
	: AuthenticationProviderBase(name: "user-certificate", diagnosticsName: "UserCertificateAuthentication")
{
	public override Task Initialize() =>
		inner.Initialize();

	public override void Authenticate(AuthenticationRequest authenticationRequest)
	{
		if (!authenticationRequest.HasValidClientCertificate)
		{
			authenticationRequest.Unauthorized();
			return;
		}

		inner.Authenticate(authenticationRequest);
	}

	public override IEnumerable<KeyValuePair<string, string>> GetPublicProperties() =>
		inner.GetPublicProperties() ?? [];

	public override void ConfigureEndpoints(IEndpointRouteBuilder endpointRouteBuilder) =>
		inner.ConfigureEndpoints(endpointRouteBuilder);

	public override void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
		inner.ConfigureServices(services, configuration);

	public override void ConfigureApplication(IApplicationBuilder app, IConfiguration configuration) =>
		inner.ConfigureApplication(app, configuration);

	public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["UserCertificate"];
}
