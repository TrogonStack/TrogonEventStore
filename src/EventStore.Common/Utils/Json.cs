using System;
using System.Text.Json;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Formatting = Newtonsoft.Json.Formatting;

namespace EventStore.Common.Utils;

public static class Json
{
	public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
	{
		ContractResolver = new CamelCasePropertyNamesContractResolver(),
		DateFormatHandling = DateFormatHandling.IsoDateFormat,
		NullValueHandling = NullValueHandling.Ignore,
		DefaultValueHandling = DefaultValueHandling.Ignore,
		MissingMemberHandling = MissingMemberHandling.Ignore,
		TypeNameHandling = TypeNameHandling.None,
		Converters = new JsonConverter[] { new StringEnumConverter() }
	};

	private static readonly JsonReaderOptions Utf8JsonReaderOptions = new() {
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip,
		MaxDepth = 64,
	};

	public static byte[] ToJsonBytes(this object source)
	{
		string instring = JsonConvert.SerializeObject(source, Formatting.Indented, JsonSettings);
		return Helper.UTF8NoBom.GetBytes(instring);
	}

	public static string ToJson(this object source)
	{
		string instring = JsonConvert.SerializeObject(source, Formatting.Indented, JsonSettings);
		return instring;
	}

	public static string ToCanonicalJson(this object source)
	{
		string instring = JsonConvert.SerializeObject(source);
		return instring;
	}

	public static T ParseJson<T>(this string json)
	{
		var result = JsonConvert.DeserializeObject<T>(json, JsonSettings);
		return result;
	}

	public static T ParseJson<T>(this byte[] json)
	{
		var result = JsonConvert.DeserializeObject<T>(Helper.UTF8NoBom.GetString(json), JsonSettings);
		return result;
	}

	public static T ParseJson<T>(this ReadOnlyMemory<byte> json)
	{
		var result = JsonConvert.DeserializeObject<T>(Helper.UTF8NoBom.GetString(json.Span), JsonSettings);
		return result;
	}

	public static object DeserializeObject(JObject value, Type type, JsonSerializerSettings settings)
	{
		Newtonsoft.Json.JsonSerializer jsonSerializer = Newtonsoft.Json.JsonSerializer.Create(settings);
		return jsonSerializer.Deserialize(new JTokenReader(value), type);
	}

	public static object DeserializeObject(JObject value, Type type, params JsonConverter[] converters)
	{
		var settings = converters == null || converters.Length <= 0
			? null
			: new JsonSerializerSettings { Converters = converters };
		return DeserializeObject(value, type, settings);
	}

	public static XmlDocument ToXmlDocument(this JObject value, string deserializeRootElementName,
		bool writeArrayAttribute)
	{
		return (XmlDocument)DeserializeObject(value, typeof(XmlDocument), new JsonConverter[] {
			new XmlNodeConverter {
				DeserializeRootElementName = deserializeRootElementName,
				WriteArrayAttribute = writeArrayAttribute
			}
		});
	}

	public static bool IsValidJson(this string value)
	{
		try
		{
			JToken.Parse(value);
		}
		catch
		{
			return false;
		}

		return true;
	}

	public static bool IsValidJson(this ReadOnlyMemory<byte> value)
	{
		return value.IsValidUtf8Json();
	}

	public static bool IsValidUtf8Json(this ReadOnlyMemory<byte> value)
	{
		if (value.IsEmpty)
			return false;

		try
		{
			var reader = new Utf8JsonReader(value.Span, Utf8JsonReaderOptions);
			while (reader.Read())
				reader.Skip();
			return true;
		}
		catch
		{
			return false;
		}
	}
}
