using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using EventStore.ClientAPI;
using EventStore.Core.Tests.Http.Users.users;
using EventStore.Transport.Http;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace EventStore.Core.Tests.Http.PersistentSubscription;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
class
	when_getting_statistics_for_new_subscription_for_stream_with_existing_events<TLogFormat, TStreamId> : with_subscription_having_events<TLogFormat, TStreamId>
{
	private JArray _json;

	protected override async Task When()
	{
		_json = await GetJson<JArray>("/subscriptions", accept: ContentType.Json);
	}

	[Test]
	[Retry(5)]
	public void returns_ok()
	{
		Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
	}

	[Test]
	[Retry(5)]
	public void should_reflect_the_known_number_of_events_in_the_stream()
	{
		var knownNumberOfEvents = _json[0]["lastKnownEventNumber"].Value<int>() + 1;
		Assert.AreEqual(Events.Count, knownNumberOfEvents,
			"Expected the subscription statistics to know about {0} events but seeing {1}", Events.Count,
			knownNumberOfEvents);
	}
}

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
class when_getting_all_statistics_in_json<TLogFormat, TStreamId> : with_subscription_having_events<TLogFormat, TStreamId>
{
	private JArray _json;

	protected override async Task When()
	{
		_json = await GetJson<JArray>("/subscriptions", accept: ContentType.Json);
	}

	[Test]
	[Retry(5)]
	public void returns_ok()
	{
		Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
	}

	[Test]
	[Retry(5)]
	public void body_contains_valid_json()
	{
		Assert.AreEqual(TestStreamName, _json[0]["eventStreamId"].Value<string>());
	}
}

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
class when_getting_all_statistics_in_xml<TLogFormat, TStreamId> : with_subscription_having_events<TLogFormat, TStreamId>
{
	private XDocument _xml;

	protected override async Task When()
	{
		_xml = await GetXml(MakeUrl("/subscriptions"));
	}

	[Test]
	[Retry(5)]
	public void returns_ok()
	{
		Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
	}

	[Test]
	[Retry(5)]
	public void body_contains_valid_xml()
	{
		Assert.AreEqual(TestStreamName, _xml.Descendants("EventStreamId").First().Value);
	}
}

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
class when_getting_subscription_stats_summary<TLogFormat, TStreamId> : SpecificationWithPersistentSubscriptionAndConnections<TLogFormat, TStreamId>
{
	private readonly PersistentSubscriptionSettings _settings = PersistentSubscriptionSettings.Create()
		.DoNotResolveLinkTos()
		.StartFromCurrent();

	private JArray _json;

	protected override async Task Given()
	{
		await base.Given();
		await _conn.CreatePersistentSubscriptionAsync(_streamName, "secondgroup", _settings,
			DefaultData.AdminCredentials);
		_conn.ConnectToPersistentSubscription(_streamName, "secondgroup",
			(subscription, @event) =>
			{
				Console.WriteLine();
				return Task.CompletedTask;
			},
			(subscription, reason, arg3) => Console.WriteLine(),
			DefaultData.AdminCredentials);
		_conn.ConnectToPersistentSubscription(_streamName, "secondgroup",
			(subscription, @event) =>
			{
				Console.WriteLine();
				return Task.CompletedTask;
			},
			(subscription, reason, arg3) => Console.WriteLine(),
			DefaultData.AdminCredentials);
		_conn.ConnectToPersistentSubscription(_streamName, "secondgroup",
			(subscription, @event) =>
			{
				Console.WriteLine();
				return Task.CompletedTask;
			},
			(subscription, reason, arg3) => Console.WriteLine(),
			DefaultData.AdminCredentials);
	}

	protected override async Task When()
	{
		_json = await GetJson<JArray>("/subscriptions", ContentType.Json);
	}

	[Test]
	[Retry(5)]
	public void the_response_code_is_ok()
	{
		Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
	}

	[Test]
	[Retry(5)]
	public void the_first_event_stream_is_correct()
	{
		Assert.AreEqual(_streamName, _json[0]["eventStreamId"].Value<string>());
	}

	[Test]
	[Retry(5)]
	public void the_first_groupname_is_correct()
	{
		Assert.AreEqual(_groupName, _json[0]["groupName"].Value<string>());
	}

	[Test]
	[Retry(5)]
	public void the_first_event_stream_has_no_http_detail_links()
	{
		Assert.AreEqual(0,
			_json[0]["links"].Count());
	}

	[Test]
	[Retry(5)]
	public void the_second_event_stream_has_no_http_detail_links()
	{
		Assert.AreEqual(0,
			_json[1]["links"].Count());
	}

	[Test]
	[Retry(5)]
	public void the_status_is_live()
	{
		Assert.AreEqual("Live", _json[0]["status"].Value<string>());
	}

	[Test]
	[Retry(5)]
	public void there_are_two_connections()
	{
		Assert.AreEqual(2, _json[0]["connectionCount"].Value<int>());
	}

	[Test]
	[Retry(5)]
	public void the_second_subscription_event_stream_is_correct()
	{
		Assert.AreEqual(_streamName, _json[1]["eventStreamId"].Value<string>());
	}

	[Test]
	[Retry(5)]
	public void the_second_subscription_groupname_is_correct()
	{
		Assert.AreEqual("secondgroup", _json[1]["groupName"].Value<string>());
	}

	[Test]
	[Retry(5)]
	public void second_subscription_there_are_three_connections()
	{
		Assert.AreEqual(3, _json[1]["connectionCount"].Value<int>());
	}
}

public abstract class SpecificationWithPersistentSubscriptionAndConnections<TLogFormat, TStreamId> : with_admin_user<TLogFormat, TStreamId>
{
	protected string _streamName = Guid.NewGuid().ToString();
	protected string _groupName = Guid.NewGuid().ToString();
	protected IEventStoreConnection _conn;
	protected EventStorePersistentSubscriptionBase _sub1;
	protected EventStorePersistentSubscriptionBase _sub2;

	private readonly PersistentSubscriptionSettings _settings = PersistentSubscriptionSettings.Create()
		.DoNotResolveLinkTos()
		.StartFromCurrent();

	protected override async Task Given()
	{
		_conn = EventStoreConnection.Create(ConnectionSettings.Create().DisableServerCertificateValidation().Build(),
			_node.TcpEndPoint);
		await _conn.ConnectAsync();
		await _conn.CreatePersistentSubscriptionAsync(_streamName, _groupName, _settings,
			DefaultData.AdminCredentials);
		_sub1 = _conn.ConnectToPersistentSubscription(_streamName, _groupName,
			(subscription, @event) =>
			{
				Console.WriteLine();
				return Task.CompletedTask;
			},
			(subscription, reason, arg3) => Console.WriteLine(), DefaultData.AdminCredentials);
		_sub2 = _conn.ConnectToPersistentSubscription(_streamName, _groupName,
			(subscription, @event) =>
			{
				Console.WriteLine();
				return Task.CompletedTask;
			},
			(subscription, reason, arg3) => Console.WriteLine(),
			DefaultData.AdminCredentials);
	}

	protected override Task When() => Task.CompletedTask;

	[OneTimeTearDown]
	public async Task Teardown()
	{
		await _conn.DeletePersistentSubscriptionAsync(_streamName, _groupName, DefaultData.AdminCredentials);
		_conn.Close();
		_conn.Dispose();
	}
}
