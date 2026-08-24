using EventStore.Client.Streams;
using Google.Protobuf.Reflection;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture]
public class SubscriptionStatusContractTests
{
	[Test]
	public void caught_up_has_timestamp_and_optional_checkpoint_context() =>
		AssertStatusContract(ReadResp.Types.CaughtUp.Descriptor);

	[Test]
	public void fell_behind_has_timestamp_and_optional_checkpoint_context() =>
		AssertStatusContract(ReadResp.Types.FellBehind.Descriptor);

	private static void AssertStatusContract(MessageDescriptor descriptor)
	{
		var timestamp = descriptor.FindFieldByNumber(1);
		var streamRevision = descriptor.FindFieldByNumber(2);
		var position = descriptor.FindFieldByNumber(3);

		Assert.Multiple(() =>
		{
			Assert.That(timestamp.Name, Is.EqualTo("timestamp"));
			Assert.That(timestamp.MessageType.FullName, Is.EqualTo("google.protobuf.Timestamp"));
			Assert.That(streamRevision.Name, Is.EqualTo("stream_revision"));
			Assert.That(streamRevision.FieldType, Is.EqualTo(FieldType.Int64));
			Assert.That(streamRevision.HasPresence, Is.True);
			Assert.That(position.Name, Is.EqualTo("position"));
			Assert.That(position.MessageType, Is.EqualTo(ReadResp.Types.Position.Descriptor));
			Assert.That(position.HasPresence, Is.True);
		});
	}
}
