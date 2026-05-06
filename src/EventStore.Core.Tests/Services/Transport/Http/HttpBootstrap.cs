using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Http;

namespace EventStore.Core.Tests.Services.Transport.Http;

public class HttpBootstrap
{
	public static void Subscribe(ISubscriber bus, KestrelHttpService service)
	{
		bus.Subscribe<SystemMessage.SystemInit>(service);
		bus.Subscribe<SystemMessage.BecomeShuttingDown>(service);
	}

	public static void Unsubscribe(ISubscriber bus, KestrelHttpService service)
	{
		bus.Unsubscribe<SystemMessage.SystemInit>(service);
		bus.Unsubscribe<SystemMessage.BecomeShuttingDown>(service);
	}
}
