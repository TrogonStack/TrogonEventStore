using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace EventStore.Core;

public static class OptionsFormatter
{
	private static readonly JsonSerializerSettings SerializerSettings = CreateSerializerSettings();
	private const string RedactedValue = "***REDACTED***";
	private static readonly string[] SensitivePropertyNames = [
		"password",
		"secret",
		"token",
		"apikey",
		"api_key",
		"accesskey",
		"access_key",
		"secretkey",
		"secret_key",
		"credential",
		"credentials",
		"connectionstring",
		"connection_string",
	];

	public static void LogConfig(string name, object options)
	{
		var configJson = FormatOptions(options);
		Serilog.Log.ForContext(typeof(OptionsFormatter))
			.Information("{name:l} Configuration:{newline:l}{configJson:l}", name, Environment.NewLine, configJson);
	}

	private static string FormatOptions(object options)
	{
		var serializer = JsonSerializer.Create(SerializerSettings);
		var config = JToken.FromObject(options, serializer);
		RedactSensitiveValues(config);
		return config.ToString(Formatting.Indented);
	}

	private static void RedactSensitiveValues(JToken token)
	{
		switch (token)
		{
			case JObject obj:
				foreach (var property in obj.Properties())
				{
					if (IsSensitive(property.Name))
					{
						property.Value = RedactedValue;
						continue;
					}

					RedactSensitiveValues(property.Value);
				}

				break;
			case JArray array:
				foreach (var item in array)
					RedactSensitiveValues(item);
				break;
		}
	}

	private static bool IsSensitive(string propertyName)
	{
		foreach (var sensitivePropertyName in SensitivePropertyNames)
			if (propertyName.Contains(sensitivePropertyName, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
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
