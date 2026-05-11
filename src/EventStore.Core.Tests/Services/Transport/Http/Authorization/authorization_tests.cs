using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.ClientAPI;
using EventStore.Common.Utils;
using EventStore.Core.Services;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class Authorization<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture {
	private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);
	private readonly Dictionary<string, HttpClient> _httpClients = new Dictionary<string, HttpClient>();
	private TimeSpan _timeout = TimeSpan.FromSeconds(5);
	private MiniNode<TLogFormat, TStreamId> _node;

	private HttpClient CreateHttpClient(string username, string password) {
		var client = new HttpClient(_node.HttpMessageHandler, disposeHandler: false) {
			BaseAddress = new Uri($"https://{_node.HttpEndPoint}"),
			Timeout = _timeout
		};

		if (!string.IsNullOrEmpty(username)) {
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue(
					"Basic", System.Convert.ToBase64String(
						System.Text.Encoding.ASCII.GetBytes(
							$"{username}:{password}")));
		}

		return client;
	}

	private async Task<int> SendRequest(HttpClient client, HttpMethod method, string url, string body, string contentType) {
		var request = new HttpRequestMessage();
		request.Method = method;
		request.RequestUri = new Uri(url, UriKind.Relative);

		if (body != null) {
			var bodyBytes = Helper.UTF8NoBom.GetBytes(body);
			var stream = new MemoryStream(bodyBytes);
			var content = new StreamContent(stream);
			content.Headers.ContentLength = bodyBytes.Length;
			if (contentType != null) {
				content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
			}

			request.Content = content;
		}

		var result = await client.SendAsync(request);
		return (int)result.StatusCode;
	}

	private HttpMethod GetHttpMethod(string method) {
		switch (method) {
			case "GET":
				return HttpMethod.Get;
			case "POST":
				return HttpMethod.Post;
			case "PUT":
				return HttpMethod.Put;
			case "DELETE":
				return HttpMethod.Delete;
			default:
				throw new Exception("Unknown Http Method");
		}
	}

	private int GetAuthLevel(string userAuthorizationLevel) {
		switch (userAuthorizationLevel) {
			case "None":
				return 0;
			case "User":
				return 1;
			case "Ops":
				return 2;
			case "Admin":
				return 3;
			default:
				throw new Exception("Unknown authorization level");
		}
	}
	public async Task CreateUser(string username, string password) {
		for (int trial = 1; trial <= 5; trial++) {
			try {
				using var channel = GrpcChannel.ForAddress(new Uri($"https://{_node.HttpEndPoint}"),
					new GrpcChannelOptions {
						HttpClient = _node.HttpClient,
						DisposeHttpClient = false
					});
				var users = new UsersClient(channel);
				await users.CreateAsync(new CreateReq {
					Options = new CreateReq.Types.Options {
						LoginName = username,
						FullName = username,
						Password = password
					}
				}, new CallOptions(new Metadata
				{
					{"authorization", _httpClients["Admin"].DefaultRequestHeaders.Authorization.ToString()}
				}));
				break;
			}
			catch (RpcException) {
				if (trial == 5) {
					throw new Exception(string.Format("Error creating user: {0}", username));
				}
				await Task.Delay(1000);
			}
		}
	}

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp() {
		await base.TestFixtureSetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
		await _node.Start();
		await _node.WaitForTcpEndPoint().WithTimeout(ReadinessTimeout);

		using var connection = await TestConnectionLifecycle.ReconnectUntilReady(
			() => TestConnection.CreateMiniNodeClient(_node.TcpEndPoint),
			conn => conn.ReadAllEventsForwardAsync(Position.Start, 1, false, DefaultData.AdminCredentials),
			ReadinessTimeout);

		_httpClients["Admin"] = CreateHttpClient("admin", "changeit");
		_httpClients["Ops"] = CreateHttpClient("ops", "changeit");
		await CreateUser("user", "changeit");
		_httpClients["User"] = CreateHttpClient("user", "changeit");
		_httpClients["None"] = CreateHttpClient(null, null);
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown() {
		foreach (var kvp in _httpClients) {
			kvp.Value.Dispose();
		}

		if (_node != null) {
			await _node.Shutdown();
		}

		await base.TestFixtureTearDown();
	}

	[Test, Combinatorial]
	public async Task authorization_tests(
		[Values(
			"None",
			"User",
			"Ops",
			"Admin"
		)] string userAuthorizationLevel,
		[Values(
			"/-/liveness;GET;None",
			"/-/readiness;GET;None",
			"/-/metrics;GET;None",
			"/ui/assets/favicon.png;GET;None"
		)] string httpEndpointDetails
	) {
		var httpEndpointTokens = httpEndpointDetails.Split(';');
		var endpointUrl = httpEndpointTokens[0];
		var httpMethod = GetHttpMethod(httpEndpointTokens[1]);
		var requiredMinAuthorizationLevel = httpEndpointTokens[2];

		var body = GetData(httpMethod);
		var contentType = httpMethod == HttpMethod.Post || httpMethod == HttpMethod.Put || httpMethod == HttpMethod.Delete ? "application/json" : null;
		var statusCode = await SendRequest(_httpClients[userAuthorizationLevel], httpMethod, endpointUrl, body, contentType);

		if (GetAuthLevel(userAuthorizationLevel) >= GetAuthLevel(requiredMinAuthorizationLevel)) {
			Assert.AreNotEqual(401, statusCode);
		}
		else {
			if (statusCode >= 300 && statusCode < 400) {
				//Redirects are always allowed because authorization is done on the canonical url
				Assert.GreaterOrEqual(statusCode, 300);
				Assert.LessOrEqual(statusCode, 307);
			}
			else {
				if (userAuthorizationLevel == "None") {
					Assert.GreaterOrEqual(statusCode, 401);
					Assert.LessOrEqual(statusCode, 403);
				}
				else {
					Assert.AreEqual(401, statusCode);
				}
			}
		}
	}

	private string GetData(HttpMethod httpMethod) {
		if (httpMethod == HttpMethod.Post || httpMethod == HttpMethod.Put || httpMethod == HttpMethod.Delete) {
			return "{}";
		}
		else {
			return null;
		}
	}
}
