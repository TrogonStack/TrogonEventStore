using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core.Tests.Helpers;

internal static class WebHostBuilderExtensions
{
	public static IWebHostBuilder UseStartup(this IWebHostBuilder builder, IStartup startup)
		=> builder
			.ConfigureServices(services => services.AddSingleton(startup));
}
