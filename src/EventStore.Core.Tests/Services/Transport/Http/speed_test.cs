using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EventStore.Common.Utils;
using EventStore.Core.Services.Transport.Http;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;

namespace EventStore.Core.Tests.Services.Transport.Http;

public class FakeController : IHttpController
{
	private readonly IUriRouter _router;

	public static readonly ICodec[] SupportedCodecs = new ICodec[]
		{Codec.Json, Codec.Xml, Codec.ApplicationXml, Codec.Text};

	private IHttpService _http;

	public readonly List<Tuple<string, string>> BoundRoutes = new List<Tuple<string, string>>();
	public readonly CountdownEvent CountdownEvent;

	public FakeController(int reqCount, IUriRouter router)
	{
		_router = router;
		CountdownEvent = new CountdownEvent(reqCount);
	}

	public void Subscribe(IHttpService http)
	{
		_http = http;

		Register("/", HttpMethod.Get);
		Register("/ping", HttpMethod.Get);
		Register("/halt", HttpMethod.Get);
		Register("/shutdown", HttpMethod.Get);
		Register("/routes/{stream}", HttpMethod.Post);
		Register("/routes/{stream}", HttpMethod.Delete);
		Register("/routes/{stream}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/{event}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/{event}/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/{event}/backward/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/{event}/forward/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/{event}/data", HttpMethod.Get);
		Register("/routes/{stream}/{event}/metadata", HttpMethod.Get);
		Register("/routes/{stream}/metadata", HttpMethod.Post);
		Register("/routes/{stream}/metadata?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/metadata/{event}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/metadata/{event}/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/metadata/{event}/backward/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/metadata/{event}/forward/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/{stream}/metadata/data", HttpMethod.Get);
		Register("/routes/{stream}/metadata/metadata", HttpMethod.Get);
		Register("/routes/{stream}/metadata/{event}/data", HttpMethod.Get);
		Register("/routes/{stream}/metadata/{event}/metadata", HttpMethod.Get);
		Register("/routes/$all?embed={embed}", HttpMethod.Get);
		Register("/routes/$all/{position}/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/$all/{position}/backward/{count}?embed={embed}", HttpMethod.Get);
		Register("/routes/$all/{position}/forward/{count}?embed={embed}", HttpMethod.Get);
		Register("/projections/any", HttpMethod.Get);
		Register("/projections/all-non-transient", HttpMethod.Get);
		Register("/projections/transient", HttpMethod.Get);
		Register("/projections/onetime", HttpMethod.Get);
		Register("/projections/continuous", HttpMethod.Get);
		Register("/projections/transient?name={name}&type={type}&enabled={enabled}", HttpMethod.Post);
		Register(
			"/projections/onetime?name={name}&type={type}&enabled={enabled}&checkpoints={checkpoints}&emit={emit}",
			HttpMethod.Post);
		Register("/projections/continuous?name={name}&type={type}&enabled={enabled}&emit={emit}", HttpMethod.Post);
		Register("/projection/{name}/query?config={config}", HttpMethod.Get);
		Register("/projection/{name}/query?type={type}&emit={emit}", HttpMethod.Put);
		Register("/projection/{name}", HttpMethod.Get);
		Register(
			"/projection/{name}?deleteStateStream={deleteStateStream}&deleteCheckpointStream={deleteCheckpointStream}",
			HttpMethod.Delete);
		Register("/projection/{name}/statistics", HttpMethod.Get);
		Register("/projection/{name}/debug", HttpMethod.Get);
		Register("/projection/{name}/state?partition={partition}", HttpMethod.Get);
		Register("/projection/{name}/result?partition={partition}", HttpMethod.Get);
		Register("/projection/{name}/command/disable", HttpMethod.Post);
		Register("/projection/{name}/command/enable", HttpMethod.Post);
		Register("/projection/{name}/command/reset", HttpMethod.Post);
		Register("/stats", HttpMethod.Get);
		Register("/stats/{*statPath}", HttpMethod.Get);
		Register("/users/", HttpMethod.Get);
	}

	private void Register(string route, string verb)
	{
		if (_router == null)
		{
			_http.RegisterAction(
				new ControllerAction(route, verb, Codec.NoCodecs, SupportedCodecs, new Operation()),
				(x, y) =>
				{
					x.Reply(new byte[0], 200, "", "", Helper.UTF8NoBom, null, e => new Exception());
					CountdownEvent.Signal();
				});
		}
		else
		{
			_router.RegisterAction(
				new ControllerAction(route, verb, Codec.NoCodecs, SupportedCodecs, new Operation()),
				(x, y) =>
				{
					CountdownEvent.Signal();
					return new RequestParams(TimeSpan.Zero);
				});
		}

		var uriTemplate = new UriTemplate(route);
		var bound = uriTemplate.BindByPosition(new Uri("http://localhost:12345/"),
			Enumerable.Range(0,
					uriTemplate.PathSegmentVariableNames.Count +
					uriTemplate.QueryValueVariableNames.Count)
				.Select(x => "abacaba")
				.ToArray());
		BoundRoutes.Add(Tuple.Create(bound.AbsoluteUri, verb));
	}
}
