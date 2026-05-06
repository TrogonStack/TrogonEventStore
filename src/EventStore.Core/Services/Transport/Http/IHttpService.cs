using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Transport.Http.EntityManagement;
using Microsoft.AspNetCore.Http;

namespace EventStore.Core.Services.Transport.Http {
	public interface IHttpSender {
		void SubscribeSenders(HttpMessagePipe pipe);
	}

	public interface IHttpForwarder {
		bool ForwardRequest(HttpEntityManager manager);
	}

	public interface IHttpService {
		ServiceAccessibility Accessibility { get; }
		bool IsListening { get; }
		IEnumerable<EndPoint> EndPoints { get; }

		void RegisterCustomAction(ControllerAction action,
			Func<HttpEntityManager, UriTemplateMatch, RequestParams> handler);

		void RegisterAction(ControllerAction action, Action<HttpEntityManager, UriTemplateMatch> handler);

		void Shutdown();

	}
}
