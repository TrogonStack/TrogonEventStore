using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventStore.ClusterNode.Components.Services;

internal static class ClusterStatusJson {
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
		Converters = {
			new JsonStringEnumConverter()
		}
	};
}
