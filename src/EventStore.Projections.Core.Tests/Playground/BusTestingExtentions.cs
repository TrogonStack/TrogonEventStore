using System;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;

namespace EventStore.Projections.Core.Tests.Playground;

public static class BusTestingExtentions
{
	private class Handler<T> : IHandle<T> where T : Message
	{
		private readonly Action<T> _handler;

		public Handler(Action<T> handler)
		{
			_handler = handler;
		}

		public void Handle(T message)
		{
			_handler(message);
		}
	}

	public static void Subscribe<T>(this ISubscriber self, Action<T> handler) where T : Message
	{
		self.Subscribe(new Handler<T>(handler));
	}
}
