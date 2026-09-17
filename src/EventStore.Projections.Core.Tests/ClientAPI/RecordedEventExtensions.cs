using EventStore.Client.Streams;
using Google.Protobuf;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using RecordedEvent = EventStore.Client.Streams.ReadResp.Types.ReadEvent.Types.RecordedEvent;

namespace EventStore.Projections.Core.Tests.ClientAPI;

internal static class RecordedEventExtensions
{
	public static string DebugDataView(this RecordedEvent source) => source.Data.ToStringUtf8();

	public static string DebugMetadataView(this RecordedEvent source) => source.CustomMetadata.ToStringUtf8();

	public static string EventType(this RecordedEvent source) =>
		source.Metadata.TryGetValue(GrpcMetadata.Type, out var eventType)
			? eventType
			: string.Empty;
}
