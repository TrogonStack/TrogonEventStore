using EventStore.Core.Messages;
using EventStore.Transport.Http.Codecs;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture]
public class message_codec_should {
	[OneTimeSetUp]
	public void SetUp() {
		HttpMessage.TextMessage.LabelStatic = "Http";
	}

	[Test]
	public void round_trip_json_without_serializing_cancellation_token() {
		var encoded = Codec.Json.To(new HttpMessage.TextMessage("Hello World!"));
		var decoded = Codec.Json.From<HttpMessage.TextMessage>(encoded);

		StringAssert.DoesNotContain("cancellationToken", encoded);
		Assert.AreEqual("Hello World!", decoded.Text);
		Assert.AreEqual("Http", decoded.Label);
	}

	[Test]
	public void round_trip_xml_without_serializing_cancellation_token() {
		var encoded = Codec.Xml.To(new HttpMessage.TextMessage("Hello World!"));
		var decoded = Codec.Xml.From<HttpMessage.TextMessage>(encoded);

		StringAssert.DoesNotContain("CancellationToken", encoded);
		Assert.AreEqual("Hello World!", decoded.Text);
		Assert.AreEqual("Http", decoded.Label);
	}
}
