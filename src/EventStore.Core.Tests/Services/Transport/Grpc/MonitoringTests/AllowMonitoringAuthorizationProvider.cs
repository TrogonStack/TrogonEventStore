using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Plugins.Authorization;

namespace EventStore.Core.Tests.Services.Transport.Grpc.MonitoringTests;

internal sealed class AllowMonitoringAuthorizationProvider : AuthorizationProviderBase
{
	public override ValueTask<bool> CheckAccessAsync(ClaimsPrincipal principal, Operation operation,
		CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
