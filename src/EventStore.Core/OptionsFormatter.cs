using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace EventStore.Core;

public static class OptionsFormatter
{
	private static readonly JsonSerializerSettings SerializerSettings = CreateSerializerSettings();

	public static void LogConfig(string name, object options)
	{
		var configJson = JsonConvert.SerializeObject(options, Formatting.Indented, SerializerSettings);
		Serilog.Log.ForContext(typeof(OptionsFormatter))
			.Information("{name:l} Configuration:{newline:l}{configJson:l}", name, Environment.NewLine, configJson);
	}

	private static JsonSerializerSettings CreateSerializerSettings()
	{
		var serializerSettings = new JsonSerializerSettings
		{
			NullValueHandling = NullValueHandling.Ignore,
		};
		serializerSettings.Converters.Add(new StringEnumConverter());
		return serializerSettings;
	}
}
