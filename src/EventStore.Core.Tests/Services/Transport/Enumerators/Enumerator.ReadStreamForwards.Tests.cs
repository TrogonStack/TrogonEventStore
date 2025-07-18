using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Enumerators;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Enumerators;

[TestFixture]
public partial class EnumeratorTests
{
	private static EnumeratorWrapper ReadStreamForwards(IPublisher publisher, string streamName)
	{
		return new EnumeratorWrapper(new Enumerator.ReadStreamForwards(
			bus: publisher,
			streamName: streamName,
			startRevision: StreamRevision.Start,
			maxCount: 10,
			resolveLinks: false,
			user: SystemAccounts.System,
			requiresLeader: false,
			deadline: DateTime.Now,
			compatibility: 1,
			cancellationToken: CancellationToken.None));
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	[TestFixture(typeof(LogFormat.V3), typeof(uint))]
	public class read_stream_forwards<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
	{
		private readonly List<Guid> _eventIds = new();

		protected override void Given()
		{
			EnableReadAll();
			_eventIds.Add(WriteEvent("test-stream", "type1", "{}", "{Data: 1}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream-all", "type1", "{}", "{Data: 1}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream", "type2", "{}", "{Data: 2}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream", "type3", "{}", "{Data: 3}").Item1.EventId);
		}

		[Test]
		public async Task should_read_forwards_from_a_particular_stream()
		{
			await using var enumerator = ReadStreamForwards(_publisher, "test-stream");

			Assert.AreEqual(_eventIds[0], ((Event)await enumerator.GetNext()).Id);
			Assert.AreEqual(_eventIds[2], ((Event)await enumerator.GetNext()).Id);
			Assert.AreEqual(_eventIds[3], ((Event)await enumerator.GetNext()).Id);
		}
	}
}
