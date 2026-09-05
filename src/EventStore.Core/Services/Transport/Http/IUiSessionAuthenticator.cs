using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace EventStore.Core.Services.Transport.Http;

public interface IUiSessionAuthenticator
{
	Task<ClaimsPrincipal> AuthenticateAsync(HttpContext context);
	Task<bool> ValidateRequestAsync(HttpContext context);
}
