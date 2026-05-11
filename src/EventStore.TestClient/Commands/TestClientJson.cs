using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace EventStore.TestClient.Commands;

internal static class TestClientJson {
	private static readonly JsonSerializerSettings FromSettings = new() {
		ContractResolver = new CamelCasePropertyNamesContractResolver(),
		DateParseHandling = DateParseHandling.None,
		NullValueHandling = NullValueHandling.Ignore,
		DefaultValueHandling = DefaultValueHandling.Include,
		MissingMemberHandling = MissingMemberHandling.Ignore,
		TypeNameHandling = TypeNameHandling.None,
		MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
		Converters = [new StringEnumConverter()]
	};

	private static readonly JsonSerializerSettings ToSettings = new() {
		ContractResolver = new CamelCasePropertyNamesContractResolver(),
		DateFormatHandling = DateFormatHandling.IsoDateFormat,
		NullValueHandling = NullValueHandling.Include,
		DefaultValueHandling = DefaultValueHandling.Include,
		MissingMemberHandling = MissingMemberHandling.Ignore,
		TypeNameHandling = TypeNameHandling.None,
		Converters = [new StringEnumConverter()]
	};

	public static T From<T>(string text) =>
		JsonConvert.DeserializeObject<T>(text, FromSettings);

	public static string To<T>(T value) =>
		value is null ? "" : JsonConvert.SerializeObject(value, Formatting.Indented, ToSettings);
}
