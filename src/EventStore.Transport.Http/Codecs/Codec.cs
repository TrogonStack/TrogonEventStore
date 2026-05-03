using System.Text;
using EventStore.Common.Utils;

namespace EventStore.Transport.Http.Codecs;

public static class Codec
{
	public static readonly NoCodec NoCodec = new NoCodec();
	public static readonly ICodec[] NoCodecs = new ICodec[0];
	public static readonly ManualEncoding ManualEncoding = new ManualEncoding();

	public static readonly JsonCodec Json = new JsonCodec();
	public static readonly XmlCodec Xml = new XmlCodec();

	public static readonly CustomCodec ApplicationXml =
		new CustomCodec(Xml, ContentType.ApplicationXml, Helper.UTF8NoBom, false, false);

	public static readonly TextCodec Text = new TextCodec();

	public static ICodec CreateCustom(ICodec codec, string contentType, Encoding encoding, bool hasEventIds,
		bool hasEventTypes)
	{
		return new CustomCodec(codec, contentType, encoding, hasEventIds, hasEventTypes);
	}
}
