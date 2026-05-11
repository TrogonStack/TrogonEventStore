using System.Collections.Generic;
using System.Net;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Transport.Http {
	public class KestrelHttpService : IHttpService,
		IHandle<SystemMessage.SystemInit>,
		IHandle<SystemMessage.BecomeShuttingDown> {
		public bool IsListening => _isListening;
		public IEnumerable<EndPoint> EndPoints { get; }

		private readonly IPublisher _inputBus;
		private string ServiceName => $"HttpServer [{string.Join(", ", EndPoints)}]";

		private bool _isListening;

		public KestrelHttpService(IPublisher inputBus, params EndPoint[] endPoints) {
			Ensure.NotNull(inputBus, nameof(inputBus));
			Ensure.NotNull(endPoints, nameof(endPoints));

			_inputBus = inputBus;

			EndPoints = endPoints;
		}

		public void Handle(SystemMessage.SystemInit message) {
			_isListening = true;
			_inputBus.Publish(new SystemMessage.ServiceInitialized(ServiceName));
		}

		public void Handle(SystemMessage.BecomeShuttingDown message) {
			if (!_isListening) {
				return;
			}

			if (message.ShutdownHttp) {
				Shutdown();
			}

			_inputBus.Publish(new SystemMessage.ServiceShutdown(ServiceName));
		}

		public void Shutdown() {
			_isListening = false;
		}
	}
}
