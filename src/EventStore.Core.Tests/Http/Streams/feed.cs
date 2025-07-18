using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Common;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Http.Users.users;
using EventStore.Transport.Http;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace EventStore.Core.Tests.Http.Streams
{
	namespace feed
	{
		public abstract class SpecificationWithLongFeed<TLogFormat, TStreamId>
			: with_admin_user<TLogFormat, TStreamId>
		{
			protected int _numberOfEvents;

			protected override async Task Given()
			{
				_numberOfEvents = 25;
				for (var i = 0; i < _numberOfEvents; i++)
				{
					await PostEvent(i);
				}
			}

			protected async Task<string> PostEvent(int i)
			{
				var response = await MakeArrayEventsPost(
					TestStream,
					new[] { new { EventId = Guid.NewGuid(), EventType = "event-type", Data = new { Number = i } } });
				Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
				return response.Headers.GetLocationAsString();
			}

			protected string GetLink(JObject feed, string relation)
			{
				var rel = (from JObject link in feed["links"]
						   from JProperty attr in link
						   where attr.Name == "relation" && (string)attr.Value == relation
						   select link).SingleOrDefault();
				return (rel == null) ? (string)null : (string)rel["uri"];
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_posting_multiple_events<TLogFormat, TStreamId> : SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			protected override Task When()
			{
				return GetJson<JObject>(TestStream, ContentType.AtomJson);
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_retrieving_feed_head<TLogFormat, TStreamId> : SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(TestStream, ContentType.AtomJson);
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void contains_a_link_rel_previous()
			{
				var rel = GetLink(_feed, "previous");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void contains_a_link_rel_next()
			{
				var rel = GetLink(_feed, "next");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void contains_a_link_rel_self()
			{
				var rel = GetLink(_feed, "self");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void contains_a_link_rel_first()
			{
				var rel = GetLink(_feed, "first");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void contains_a_link_rel_last()
			{
				var rel = GetLink(_feed, "last");
				Assert.IsNotEmpty(rel);
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_retrieving_feed_head_with_forwarded_prefix<TLogFormat, TStreamId> : SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private string _prefix;

			protected override async Task When()
			{
				_prefix = "testprefix";
				var headers = new NameValueCollection();
				headers.Add("X-Forwarded-Prefix", _prefix);
				_feed = await GetJson<JObject>(TestStream, ContentType.AtomJson, headers: headers);
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void contains_a_link_rel_previous_with_prefix()
			{
				var rel = GetLink(_feed, "previous");
				Assert.AreEqual(MakeUrl("/" + _prefix + TestStream + "/25/forward/20"), new Uri(rel));
			}

			[Test]
			public void contains_a_link_rel_next_with_prefix()
			{
				var rel = GetLink(_feed, "next");
				Assert.AreEqual(MakeUrl("/" + _prefix + TestStream + "/4/backward/20"), new Uri(rel));
			}

			[Test]
			public void contains_a_link_rel_self_with_prefix()
			{
				var rel = GetLink(_feed, "self");
				Assert.AreEqual(MakeUrl("/" + _prefix + TestStream), new Uri(rel));
			}

			[Test]
			public void contains_a_link_rel_first_with_prefix()
			{
				var rel = GetLink(_feed, "first");
				Assert.AreEqual(MakeUrl("/" + _prefix + TestStream + "/head/backward/20"), new Uri(rel));
			}

			[Test]
			public void contains_a_link_rel_last_with_prefix()
			{
				var rel = GetLink(_feed, "last");
				Assert.AreEqual(MakeUrl("/" + _prefix + TestStream + "/0/forward/20"), new Uri(rel));
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_retrieving_the_previous_link_of_the_feed_head<TLogFormat, TStreamId> : SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private JObject _head;
			private string _previous;

			protected override async Task Given()
			{
				await base.Given();
				_head = await GetJson<JObject>(TestStream, ContentType.AtomJson);
				_previous = GetLink(_head, "previous");
			}

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(_previous, ContentType.AtomJson);
			}

			[Test]
			public void returns_200_response()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void there_is_no_prev_link()
			{
				var rel = GetLink(_feed, "prev");
				Assert.IsNull(rel);
			}

			[Test]
			public void there_is_a_next_link()
			{
				var rel = GetLink(_feed, "next");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void there_is_a_self_link()
			{
				var rel = GetLink(_feed, "self");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void there_is_a_first_link()
			{
				var rel = GetLink(_feed, "first");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void there_is_a_last_link()
			{
				var rel = GetLink(_feed, "last");
				Assert.IsNotEmpty(rel);
			}

			[Test]
			public void the_feed_is_empty()
			{
				Assert.AreEqual(0, _feed["entries"].Count());
			}

			[Test]
			public void the_response_is_not_cachable()
			{
				Assert.AreEqual(CacheControlHeaderValue.Parse("max-age=0, no-cache, must-revalidate"),
					_lastResponse.Headers.CacheControl);
			}
		}


		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_reading_a_stream_forward_with_deleted_linktos<TLogFormat, TStreamId>
			: HttpSpecificationWithLinkToToDeletedEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>("/streams/" + LinkedStreamName + "/0/forward/10", accept: ContentType.Json, credentials: DefaultData.AdminNetworkCredentials);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_one_event()
			{
				Assert.AreEqual(1, _entries.Count());
			}

			[Test]
			public void the_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_reading_a_stream_forward_with_linkto<TLogFormat, TStreamId>
			: HttpSpecificationWithLinkToToEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>("/streams/" + LinkedStreamName + "/0/forward/10", accept: ContentType.Json);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_two_events()
			{
				Assert.AreEqual(2, _entries.Count());
			}

			[Test]
			public void the_second_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[1]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + Stream2Name + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_second_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[1]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + Stream2Name + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_first_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + StreamName + "/1"), foo["uri"].ToString());
			}

			[Test]
			public void the_first_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + StreamName + "/1"), foo["uri"].ToString());
			}
		}


		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_reading_a_stream_forward_with_linkto_with_at_sign_in_name<TLogFormat, TStreamId>
			: HttpBehaviorSpecification<TLogFormat, TStreamId>
		{
			protected string LinkedStreamName;
			protected string StreamName;

			protected override async Task Given()
			{
				var creds = DefaultData.AdminCredentials;
				LinkedStreamName = Guid.NewGuid().ToString();
				StreamName = Guid.NewGuid() + "@" + Guid.NewGuid() + "@";
				using (var conn = TestConnection.Create(_node.TcpEndPoint))
				{
					await conn.ConnectAsync();
					await conn.AppendToStreamAsync(StreamName, ExpectedVersion.Any, creds,
							new EventData(Guid.NewGuid(), "testing", true, Encoding.UTF8.GetBytes("{'foo' : 4}"),
								new byte[0]))
;
					await conn.AppendToStreamAsync(StreamName, ExpectedVersion.Any, creds,
							new EventData(Guid.NewGuid(), "testing", true, Encoding.UTF8.GetBytes("{'foo' : 4}"),
								new byte[0]))
;
					await conn.AppendToStreamAsync(StreamName, ExpectedVersion.Any, creds,
							new EventData(Guid.NewGuid(), "testing", true, Encoding.UTF8.GetBytes("{'foo' : 4}"),
								new byte[0]))
;
					await conn.AppendToStreamAsync(LinkedStreamName, ExpectedVersion.Any, creds,
						new EventData(Guid.NewGuid(), SystemEventTypes.LinkTo, false,
							Encoding.UTF8.GetBytes("0@" + StreamName), new byte[0]));
					await conn.AppendToStreamAsync(LinkedStreamName, ExpectedVersion.Any, creds,
						new EventData(Guid.NewGuid(), SystemEventTypes.LinkTo, false,
							Encoding.UTF8.GetBytes("1@" + StreamName), new byte[0]));
				}
			}

			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>("/streams/" + LinkedStreamName + "/0/forward/10", accept: ContentType.Json);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_two_events()
			{
				Assert.AreEqual(2, _entries.Count());
			}

			[Test]
			public void the_second_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[1]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				//TODO GFY I really wish we were targeting 4.5 only so I could use Uri.EscapeDataString
				//given the choice between this and a dependency on system.web well yeah. When we have 4.5
				//only lets use Uri
				Assert.AreEqual(MakeUrl("/streams/" + StreamName.Replace("@", "%40") + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_second_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[1]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + StreamName.Replace("@", "%40") + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_first_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + StreamName.Replace("@", "%40") + "/1"), foo["uri"].ToString());
			}

			[Test]
			public void the_first_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + StreamName.Replace("@", "%40") + "/1"), foo["uri"].ToString());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class
			when_reading_a_stream_forward_with_maxcount_deleted_linktos<TLogFormat, TStreamId> :
				SpecificationWithLinkToToMaxCountDeletedEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>("/streams/" + LinkedStreamName + "/0/forward/10", accept: ContentType.Json);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_no_events()
			{
				Assert.AreEqual(1, _entries.Count());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		[Explicit("Failing test for Greg demonstrating NullReferenceException in Convert.cs")]
		public class
			when_reading_a_stream_forward_with_maxcount_deleted_linktos_with_rich_entry<TLogFormat, TStreamId> :
				SpecificationWithLinkToToMaxCountDeletedEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>("/streams/" + LinkedStreamName + "/0/forward/10", extra: "embed=rich",
					accept: ContentType.Json);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_some_events()
			{
				Assert.AreEqual(1, _entries.Count());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_reading_a_stream_forward_with_deleted_linktos_with_content_enabled_as_xml<TLogFormat, TStreamId> :
			HttpSpecificationWithLinkToToDeletedEvents<TLogFormat, TStreamId>
		{
			private XDocument _feed;
			private XElement[] _entries;

			protected override async Task When()
			{
				_feed = await GetAtomXml(MakeUrl("/streams/" + LinkedStreamName + "/0/forward/10", "embed=content"));
				_entries = _feed.GetEntries();
			}

			[Test]
			public void the_feed_has_one_event()
			{
				Assert.AreEqual(1, _entries.Length);
			}

			[Test]
			public void the_edit_link_is_to_correct_uri()
			{
				var link = _entries[0].GetLink("edit");
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), link);
			}

			[Test]
			public void the_alternate_link_is_to_correct_uri()
			{
				var link = _entries[0].GetLink("alternate");
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), link);
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class
			when_reading_a_stream_forward_with_deleted_linktos_with_content_enabled<TLogFormat, TStreamId> :
				HttpSpecificationWithLinkToToDeletedEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				var uri = MakeUrl("/streams/" + LinkedStreamName + "/0/forward/10", "embed=content");
				_feed = await GetJson<JObject>(uri.ToString(), accept: ContentType.Json, credentials: DefaultData.AdminNetworkCredentials);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_one_event()
			{
				Assert.AreEqual(1, _entries.Count());
			}

			[Test]
			public void the_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_reading_a_stream_backward_with_deleted_linktos<TLogFormat, TStreamId>
			: HttpSpecificationWithLinkToToDeletedEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>("/streams/" + LinkedStreamName + "/0/backward/1", accept: ContentType.Json, credentials: DefaultData.AdminNetworkCredentials);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_one_event()
			{
				Assert.AreEqual(1, _entries.Count());
			}

			[Test]
			public void the_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class
			when_reading_a_stream_backward_with_deleted_linktos_and_embed_of_content<TLogFormat, TStreamId> :
				HttpSpecificationWithLinkToToDeletedEvents<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private List<JToken> _entries;

			protected override async Task When()
			{
				var uri = MakeUrl("/streams/" + LinkedStreamName + "/0/backward/1", "embed=content");
				_feed = await GetJson<JObject>(uri.ToString(), accept: ContentType.Json, credentials: DefaultData.AdminNetworkCredentials);
				_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
			}

			[Test]
			public void the_feed_has_one_event()
			{
				Assert.AreEqual(1, _entries.Count());
			}

			[Test]
			public void the_edit_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][0];
				Assert.AreEqual("edit", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}

			[Test]
			public void the_alt_link_to_is_to_correct_uri()
			{
				var foo = _entries[0]["links"][1];
				Assert.AreEqual("alternate", foo["relation"].ToString());
				Assert.AreEqual(MakeUrl("/streams/" + DeletedStreamName + "/0"), foo["uri"].ToString());
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_polling_the_head_forward_and_a_new_event_appears<TLogFormat, TStreamId> : SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private JObject _head;
			private string _previous;
			private string _lastEventLocation;

			protected override async Task Given()
			{
				await base.Given();
				_head = await GetJson<JObject>(TestStream, ContentType.AtomJson);
				Console.WriteLine(new string('*', 60));
				Console.WriteLine(_head);
				Console.WriteLine(TestStream);
				Console.WriteLine(new string('*', 60));
				_previous = GetLink(_head, "previous");
				_lastEventLocation = await PostEvent(-1);
			}

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(_previous, ContentType.AtomJson);
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void returns_a_feed_with_a_single_entry_referring_to_the_last_event()
			{
				HelperExtensions.AssertJson(new { entries = new[] { new { Id = _lastEventLocation } } }, _feed);
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class
			when_reading_a_stream_forward_from_beginning_asking_for_less_events_than_in_the_stream<TLogFormat, TStreamId> :
				SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(TestStream + "/0/forward/" + (_numberOfEvents - 1),
					accept: ContentType.AtomJson);
			}

			[Test]
			public void the_head_of_stream_is_false()
			{
				Assert.AreEqual(false, _feed.Value<bool>("headOfStream"));
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class
			when_reading_a_stream_forward_from_beginning_asking_for_more_events_than_in_the_stream<TLogFormat, TStreamId> :
				SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(TestStream + "/0/forward/" + (_numberOfEvents + 1),
					accept: ContentType.AtomJson);
			}

			[Test]
			public void the_head_of_stream_is_true()
			{
				Assert.AreEqual(true, _feed.Value<bool>("headOfStream"));
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class
			when_reading_a_stream_forward_from_beginning_asking_for_exactly_the_events_in_the_stream<TLogFormat, TStreamId> :
				SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(TestStream + "/0/forward/" + _numberOfEvents, accept: ContentType.AtomJson);
			}

			[Test]
			public void the_head_of_stream_is_true()
			{
				Assert.AreEqual(true, _feed.Value<bool>("headOfStream"));
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_reading_a_stream_forward_with_etag_in_header<TLogFormat, TStreamId> : SpecificationWithLongFeed<TLogFormat, TStreamId>
		{
			private JObject _feed;

			protected override async Task When()
			{
				_feed = await GetJson<JObject>(TestStream, accept: ContentType.AtomJson);
				var etag = _feed.Value<string>("eTag");
				var headers = new NameValueCollection { { "If-None-Match", EntityTagHeaderValue.Parse($@"""{etag}""").Tag } };
				_feed = await GetJson<JObject>(TestStream, accept: ContentType.AtomJson, headers: headers);
			}

			[Test]
			public void should_return_not_modified()
			{
				Assert.AreEqual(HttpStatusCode.NotModified, _lastResponse.StatusCode);
			}
		}
	}

	[Category("LongRunning")]
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	[TestFixture(typeof(LogFormat.V3), typeof(uint))]
	public class when_reading_the_all_stream_backward_with_resolve_link_to<TLogFormat, TStreamId>
		: HttpSpecificationWithLinkToToEvents<TLogFormat, TStreamId>
	{
		private JObject _feed;
		private List<JToken> _entries;

		protected override async Task When()
		{
			var headers = new NameValueCollection { { "ES-ResolveLinkTos", "True" } };
			_feed = await GetJson<JObject>("/streams/$all/head/backward/30",
				ContentType.Json, DefaultData.AdminNetworkCredentials, headers);
			_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
		}

		[Test]
		public void there_are_three_events_for_the_first_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(StreamName));
			Assert.AreEqual(3, events.Count());
		}

		[Test]
		public void there_are_two_events_for_the_second_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(Stream2Name));
			Assert.AreEqual(2, events.Count());
		}

		[Test]
		public void there_are_no_events_for_the_linked_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(LinkedStreamName));
			Assert.IsEmpty(events);
		}
	}

	[Category("LongRunning")]
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	[TestFixture(typeof(LogFormat.V3), typeof(uint))]
	public class when_reading_the_all_stream_backward_with_resolve_link_to_disabled<TLogFormat, TStreamId>
		: HttpSpecificationWithLinkToToEvents<TLogFormat, TStreamId>
	{
		private JObject _feed;
		private List<JToken> _entries;

		protected override async Task When()
		{
			var headers = new NameValueCollection { { "ES-ResolveLinkTos", "False" } };
			_feed = await GetJson<JObject>("/streams/$all",
				ContentType.Json, DefaultData.AdminNetworkCredentials, headers);
			_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
		}

		[Test]
		public void there_are_two_events_for_the_first_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(StreamName));
			Assert.AreEqual(2, events.Count());
		}

		[Test]
		public void there_is_one_event_for_the_second_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(Stream2Name));
			Assert.AreEqual(1, events.Count());
		}

		[Test]
		public void there_are_two_events_for_the_linked_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(LinkedStreamName));
			Assert.AreEqual(2, events.Count());
		}
	}

	[Category("LongRunning")]
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	[TestFixture(typeof(LogFormat.V3), typeof(uint))]
	public class when_reading_the_all_stream_forward_with_resolve_link_to<TLogFormat, TStreamId>
		: HttpSpecificationWithLinkToToEvents<TLogFormat, TStreamId>
	{
		private JObject _feed;
		private List<JToken> _entries;

		protected override async Task When()
		{
			var headers = new NameValueCollection { { "ES-ResolveLinkTos", "True" } };
			_feed = await GetJson<JObject>("/streams/$all/00000000000000000000037777777777/forward/30",
				ContentType.Json, DefaultData.AdminNetworkCredentials, headers);
			_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
		}

		[Test]
		public void there_are_three_events_for_the_first_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(StreamName));
			Assert.AreEqual(3, events.Count());
		}

		[Test]
		public void there_are_two_events_for_the_second_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(Stream2Name));
			Assert.AreEqual(2, events.Count());
		}

		[Test]
		public void there_are_no_events_for_the_linked_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(LinkedStreamName));
			Assert.IsEmpty(events);
		}
	}

	[Category("LongRunning")]
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	[TestFixture(typeof(LogFormat.V3), typeof(uint))]
	public class when_reading_the_all_stream_forward_with_resolve_link_to_disabled<TLogFormat, TStreamId>
		: HttpSpecificationWithLinkToToEvents<TLogFormat, TStreamId>
	{
		private JObject _feed;
		private List<JToken> _entries;

		protected override async Task When()
		{
			var headers = new NameValueCollection { { "ES-ResolveLinkTos", "False" } };
			_feed = await GetJson<JObject>("/streams/$all/00000000000000000000037777777777/forward/30",
				ContentType.Json, DefaultData.AdminNetworkCredentials, headers);
			_entries = _feed != null ? _feed["entries"].ToList() : new List<JToken>();
		}

		[Test]
		public void there_are_two_events_for_the_first_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(StreamName));
			Assert.AreEqual(2, events.Count());
		}

		[Test]
		public void there_is_one_event_for_the_second_original_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(Stream2Name));
			Assert.AreEqual(1, events.Count());
		}

		[Test]
		public void there_are_two_events_for_the_linked_stream()
		{
			var events = _entries.Where(x => x["title"].Value<string>().Contains(LinkedStreamName));
			Assert.AreEqual(2, events.Count());
		}
	}
}

// This test needs to be out of the streams namespace to prevent it from inheriting the wrong mini node.
namespace EventStore.Core.Tests.Http
{
	public class when_running_the_node_advertising_a_different_ip_as
	{
		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class when_retrieving_feed_head_and_http_advertise_ip_is_set<TLogFormat, TStreamId> : with_admin_user<TLogFormat, TStreamId>
		{
			private JObject _feed;
			private string advertisedAddress = "127.0.10.1";
			private int advertisedPort = 2116;

			protected override MiniNode<TLogFormat, TStreamId> CreateMiniNode()
			{
				return new MiniNode<TLogFormat, TStreamId>(PathName,
					advertisedExtHostAddress: advertisedAddress, advertisedHttpPort: advertisedPort);
			}

			protected override async Task Given()
			{
				var response = await MakeArrayEventsPost(
					TestStream,
					new[] { new { EventId = Guid.NewGuid(), EventType = "event-type", Data = new { Number = 1 } } });
				Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
			}

			protected string GetLink(JObject feed, string relation)
			{
				var rel = (from JObject link in feed["links"]
						   from JProperty attr in link
						   where attr.Name == "relation" && (string)attr.Value == relation
						   select link).SingleOrDefault();
				return (rel == null) ? (string)null : (string)rel["uri"];
			}

			protected string GetFirstEntryLink(JObject feed)
			{
				var rel = (from JObject entry in feed["entries"]
						   from JObject link in entry["links"]
						   from JProperty attr in link
						   where attr.Name == "relation" && (string)attr.Value == "edit"
						   select link).FirstOrDefault();
				return (rel == null) ? (string)null : (string)rel["uri"];
			}

			protected override async Task When()
			{
				Console.WriteLine("Getting feed");
				_feed = await GetJson<JObject>(TestStream, ContentType.AtomJson);
				Console.WriteLine("Feed: {0}", _feed);
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void contains_a_link_rel_previous_using_advertised_ip_and_port()
			{
				var rel = GetLink(_feed, "previous");
				Assert.IsNotEmpty(rel);
				var uri = new Uri(rel);
				Assert.AreEqual(advertisedAddress.ToString(), uri.Host);
				Assert.AreEqual(advertisedPort, uri.Port);
			}

			[Test]
			public void contains_a_link_rel_self_using_advertised_ip_and_port()
			{
				var rel = GetLink(_feed, "self");
				Assert.IsNotEmpty(rel);
				var uri = new Uri(rel);
				Assert.AreEqual(advertisedAddress.ToString(), uri.Host);
				Assert.AreEqual(advertisedPort, uri.Port);
			}

			[Test]
			public void contains_a_link_rel_first_using_advertised_ip_and_port()
			{
				var rel = GetLink(_feed, "first");
				Assert.IsNotEmpty(rel);
				var uri = new Uri(rel);
				Assert.AreEqual(advertisedAddress.ToString(), uri.Host);
				Assert.AreEqual(advertisedPort, uri.Port);
			}

			[Test]
			public void contains_an_entry_with_rel_link_using_advertised_ip_and_port()
			{
				var rel = GetFirstEntryLink(_feed);
				Assert.IsNotEmpty(rel);
				var uri = new Uri(rel);
				Assert.AreEqual(advertisedAddress.ToString(), uri.Host);
				Assert.AreEqual(advertisedPort, uri.Port);
			}
		}
	}
}
