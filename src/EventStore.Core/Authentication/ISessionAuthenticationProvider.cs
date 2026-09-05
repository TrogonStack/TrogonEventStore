using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Plugins.Authentication;

namespace EventStore.Core.Authentication;

public interface ISessionAuthenticationProvider
{
	void AuthenticateSession(AuthenticationRequest authenticationRequest);
	Task<ClaimsPrincipal> ValidateSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}
