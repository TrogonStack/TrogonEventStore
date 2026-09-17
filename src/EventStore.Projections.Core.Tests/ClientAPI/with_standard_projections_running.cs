using System.Text;
using System.Threading.Tasks;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.ClientAPI
{
	namespace with_standard_projections_running
	{
		public abstract class when_deleting_stream_base<TLogFormat, TStreamId>
			: specification_with_standard_projections_runnning<TLogFormat, TStreamId>
		{
			[Test, Category("Network")]
			public async Task streams_stream_exists()
			{
				var result = await WaitForStreamEvents("$streams", 1, false);
				Assert.That(result.Exists, Is.True);
			}

			[Test, Category("Network")]
			public async Task deleted_stream_events_are_indexed()
			{
				var result = await WaitForStreamEvents("$ce-cat", 3, true);
				Assert.That(result.Events, Has.Count.EqualTo(3));

				var deletedLink = result.Events[2].Link;
				Assert.That(deletedLink, Is.Not.Null);
				Assert.That(deletedLink.CustomMetadata, Is.Not.Null);

				var checkpointTag = Encoding.UTF8.GetString(deletedLink.CustomMetadata.ToByteArray())
					.ParseCheckpointExtraJson();
				Assert.That(checkpointTag.TryGetValue("$deleted", out _), Is.True);
				Assert.That(checkpointTag.TryGetValue("$o", out var originalStream), Is.True);
				Assert.That(((JValue)originalStream).Value, Is.EqualTo("cat-1"));
			}

			[Test, Category("Network")]
			public async Task deleted_stream_events_are_indexed_as_deleted()
			{
				var result = await WaitForStreamEvents("$et-$deleted", 1, true);
				Assert.That(result.Events, Has.Count.EqualTo(1));
			}

			protected override async Task When()
			{
				await base.When();
				var firstAppend = await AppendToNewStream("cat-1", "type1", "{}");
				Assert.That(firstAppend.ResultCase, Is.EqualTo(EventStore.Client.Streams.AppendResp.ResultOneofCase.Success));

				var secondAppend = await AppendToStream(
					"cat-1",
					firstAppend.Success.CurrentRevision,
					"type1",
					"{}");
				Assert.That(secondAppend.ResultCase, Is.EqualTo(EventStore.Client.Streams.AppendResp.ResultOneofCase.Success));

				if (GivenDeleteHardDeleteStreamMode())
				{
					await HardDeleteStream("cat-1", secondAppend.Success.CurrentRevision);
				}
				else
				{
					await SoftDeleteStream("cat-1", secondAppend.Success.CurrentRevision);
				}

				if (!GivenStandardProjectionsRunning())
				{
					await EnableStandardProjections();
				}
			}

			protected abstract bool GivenDeleteHardDeleteStreamMode();
		}

		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		public class when_hard_deleting_stream<TLogFormat, TStreamId> : when_deleting_stream_base<TLogFormat, TStreamId>
		{
			protected override bool GivenDeleteHardDeleteStreamMode() => true;
		}

		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		public class when_soft_deleting_stream<TLogFormat, TStreamId> : when_deleting_stream_base<TLogFormat, TStreamId>
		{
			protected override bool GivenDeleteHardDeleteStreamMode() => false;
		}

		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		public class when_hard_deleting_stream_and_starting_standard_projections<TLogFormat, TStreamId> : when_deleting_stream_base<TLogFormat, TStreamId>
		{
			protected override bool GivenDeleteHardDeleteStreamMode() => true;

			protected override bool GivenStandardProjectionsRunning() => false;
		}

		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		public class when_soft_deleting_stream_and_starting_standard_projections<TLogFormat, TStreamId> : when_deleting_stream_base<TLogFormat, TStreamId>
		{
			protected override bool GivenDeleteHardDeleteStreamMode() => false;

			protected override bool GivenStandardProjectionsRunning() => false;
		}
	}
}
