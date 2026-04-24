using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core.Tests.Helpers;

internal static class WebHostBuilderExtensions
{
	public static IWebHostBuilder UseStartup(this IWebHostBuilder builder, IStartup startup)
	{
		if (startup is IInternalStartup internalStartup)
			return builder
				.ConfigureServices(internalStartup.ConfigureServicesOnly)
				.Configure(startup.Configure);

		return builder
			.ConfigureServices(services => services.AddSingleton(startup));
	}
}
