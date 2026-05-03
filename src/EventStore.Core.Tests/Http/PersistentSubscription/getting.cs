using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Common;
using EventStore.Common.Utils;
using EventStore.Core.Tests.Http.Users.users;
using EventStore.Transport.Http;
using NUnit.Framework;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace EventStore.Core.Tests.Http.PersistentSubscription;

abstract class with_subscription_having_events<TLogFormat, TStreamId> : with_admin_user<TLogFormat, TStreamId>
{
	protected List<EventData> Events;
	protected string SubscriptionPath;
	protected string GroupName;

	protected override async Task Given()
	{
		Events = new List<EventData> {
			CreateEvent("event-type", new {A = "1"}),
			CreateEvent("event-type", new {B = "2"}),
			CreateEvent("event-type", new {C = "3"}),
			CreateEvent("event-type", new {D = "4"})
		};

		await _connection.AppendToStreamAsync(
			TestStreamName,
			ExpectedVersion.NoStream,
			Events);

		GroupName = Guid.NewGuid().ToString();
		SubscriptionPath = string.Format("/subscriptions/{0}/{1}", TestStreamName, GroupName);
		var response = await MakeJsonPut(SubscriptionPath,
			new
			{
				ResolveLinkTos = true,
				MessageTimeoutMilliseconds = 10000,
			},
			_admin);
		Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
	}

	protected override Task When() => Task.CompletedTask;

	protected async Task SecureStream()
	{
		var metadata =
			(StreamMetadata)
			StreamMetadata.Build()
				.SetMetadataReadRole("admin")
				.SetMetadataWriteRole("admin")
				.SetReadRole("admin")
				.SetWriteRole("admin");
		var jsonMetadata = metadata.AsJsonString();
		await _connection.AppendToStreamAsync(
			EventStore.Core.Services.SystemStreams.MetastreamOf(TestStreamName),
			ExpectedVersion.NoStream,
			new EventData(
				Guid.NewGuid(),
				EventStore.Core.Services.SystemEventTypes.StreamMetadata,
				true,
				Helper.UTF8NoBom.GetBytes(jsonMetadata),
				Array.Empty<byte>()));
	}

	private EventData CreateEvent(string eventType, object data) =>
		new(Guid.NewGuid(), eventType, true, ToJsonBytes(data), Array.Empty<byte>());
}
