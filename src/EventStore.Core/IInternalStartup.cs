#nullable enable

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core;

public interface IInternalStartup : IStartup
{
	void ConfigureServicesOnly(IServiceCollection services);
}
