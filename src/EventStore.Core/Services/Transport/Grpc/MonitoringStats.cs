using System;
using System.Collections.Generic;
using System.Globalization;
using EventStore.Common.Utils;
using EventStore.Core.Services.Monitoring.Stats;
using Google.Protobuf.WellKnownTypes;

namespace EventStore.Core.Services.Transport.Grpc;

internal static class MonitoringStats {
	public static Func<Dictionary<string, object>, Dictionary<string, object>> GetStatSelector(string statPath) {
		if (string.IsNullOrEmpty(statPath))
			return dict => dict;

		if (statPath.StartsWith("stats/")) {
			statPath = statPath.Substring(6);
			if (string.IsNullOrEmpty(statPath))
				return dict => dict;
		}

		var groups = statPath.Split('/');

		return dict => {
			Ensure.NotNull(dict, "dictionary");

			foreach (var groupName in groups) {
				if (!dict.TryGetValue(groupName, out var item))
					return null;

				dict = item as Dictionary<string, object>;
				if (dict is null)
					return null;
			}

			return dict;
		};
	}

	public static Value ToValue(object value) =>
		value switch {
			null => new Value { NullValue = NullValue.NullValue },
			Value grpcValue => grpcValue,
			string stringValue => new Value { StringValue = stringValue },
			bool boolValue => new Value { BoolValue = boolValue },
			byte numberValue => ToNumberValue(numberValue),
			sbyte numberValue => ToNumberValue(numberValue),
			short numberValue => ToNumberValue(numberValue),
			ushort numberValue => ToNumberValue(numberValue),
			int numberValue => ToNumberValue(numberValue),
			uint numberValue => ToNumberValue(numberValue),
			long numberValue => ToNumberValue(numberValue),
			ulong numberValue => ToNumberValue(numberValue),
			float numberValue => ToNumberValue(numberValue),
			double numberValue => ToNumberValue(numberValue),
			decimal numberValue => ToNumberValue(numberValue),
			Dictionary<string, object> dictionary => ToStructValue(dictionary),
			IReadOnlyDictionary<string, object> dictionary => ToStructValue(dictionary),
			StatMetadata metadata => ToStructValue(new Dictionary<string, object> {
				["value"] = metadata.Value,
				["category"] = metadata.Category,
				["title"] = metadata.Title,
				["drawChart"] = metadata.DrawChart
			}),
			_ => new Value { StringValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty }
		};

	private static Value ToNumberValue<T>(T value) where T : struct, IConvertible =>
		new() { NumberValue = value.ToDouble(CultureInfo.InvariantCulture) };

	private static Value ToStructValue(IEnumerable<KeyValuePair<string, object>> values) {
		var structValue = new Struct();
		foreach (var (key, value) in values)
			structValue.Fields.Add(key, ToValue(value));

		return new Value { StructValue = structValue };
	}
}
