using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventStore.Core.Bus;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.InMemory;

public class GossipListenerService :
	IHandle<GossipMessage.GossipUpdated>
{
	private readonly Guid _nodeId;
	public const string EventType = "$GossipUpdated";

	public SingleEventInMemoryStream Stream { get; }

	private readonly JsonSerializerOptions _options = new()
	{
		Converters = {
			new JsonStringEnumConverter(),
		},
	};

	public GossipListenerService(Guid nodeId, IPublisher publisher, InMemoryLog memLog)
	{
		Stream = new(publisher, memLog, SystemStreams.GossipStream);
		_nodeId = nodeId;
	}

	public void Handle(GossipMessage.GossipUpdated message)
	{
		// SystemStreams.GossipStream is a system stream so only readable by admins
		// we use ClientMemberInfo because plugins will consume this stream and
		// it is less likely to change than the internal gossip.
		var payload = new
		{
			NodeId = _nodeId,
			Members = message.ClusterInfo.Members.Select(static x =>
				new Cluster.ClientClusterInfo.ClientMemberInfo(x)),
		};

		var data = JsonSerializer.SerializeToUtf8Bytes(payload, _options);
		Stream.Write(EventType, data);
	}
}
