using System.Text.Json;
using System.Text.Json.Serialization;
using EventStore.Core.Bus;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.InMemory;

// threading: we expect to handle one StateChangeMessage at a time, but Reads can happen concurrently
// with those handlings and with other reads.
public class NodeStateListenerService :
	IHandle<SystemMessage.StateChangeMessage>
{
	public const string EventType = "$NodeStateChanged";

	public SingleEventInMemoryStream Stream { get; }

	private readonly JsonSerializerOptions _options = new()
	{
		Converters = {
			new JsonStringEnumConverter(),
		},
	};

	public NodeStateListenerService(IPublisher publisher, InMemoryLog memLog)
	{
		Stream = new(publisher, memLog, SystemStreams.NodeStateStream);
	}

	public void Handle(SystemMessage.StateChangeMessage message)
	{
		var payload = new { message.State };
		var data = JsonSerializer.SerializeToUtf8Bytes(payload, _options);
		Stream.Write(EventType, data);
	}
}
