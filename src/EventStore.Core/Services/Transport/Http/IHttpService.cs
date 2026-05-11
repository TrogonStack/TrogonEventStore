using System.Collections.Generic;
using System.Net;

namespace EventStore.Core.Services.Transport.Http
{
	public interface IHttpService
	{
		bool IsListening { get; }
		IEnumerable<EndPoint> EndPoints { get; }

		void Shutdown();
	}
}
