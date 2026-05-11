using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EventStore.Client.Node;
using EventStore.Common.Options;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Plugins.Authentication;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EventStore.Core.Services.Transport.Grpc;

public sealed class NodeInformationProvider(
	ClusterVNodeOptions options,
	IReadOnlyDictionary<string, bool> features,
	IAuthenticationProvider authenticationProvider)
	: IHandle<SystemMessage.StateChangeMessage> {
	private int _currentState;

	public NodeInfo Read() {
		var result = new NodeInfo {
			EsVersion = VersionInfo.Version,
			State = ((VNodeState)Volatile.Read(ref _currentState)).ToString().ToLowerInvariant(),
			Authentication = AuthenticationInfo()
		};
		foreach (var (key, value) in features) {
			result.Features.Add(key, value);
		}

		return result;
	}

	public NodeOptions Options() {
		var result = new NodeOptions();
		result.Options.AddRange(options.LoadedOptions.Values.Select(ToOption));
		return result;
	}

	public string ReadJson() =>
		JsonFormatter.Default.Format(Read());

	public void Handle(SystemMessage.StateChangeMessage message) =>
		Volatile.Write(ref _currentState, (int)message.State);

	private AuthenticationInfo AuthenticationInfo() {
		if (authenticationProvider is null) {
			return new AuthenticationInfo();
		}

		var result = new AuthenticationInfo {
			Type = authenticationProvider.Name
		};
		foreach (var (key, value) in authenticationProvider.GetPublicProperties() ?? []) {
			result.Properties.Add(key, value);
		}

		return result;
	}

	private static NodeOption ToOption(LoadedOption option) =>
		new() {
			Name = option.Metadata.Name,
			Description = option.Metadata.Description,
			Group = option.Metadata.SectionMetadata.SectionType.Name,
			Value = option.DisplayValue,
			ConfigurationSource = option.SourceDisplayName,
			DeprecationMessage = option.Metadata.DeprecationMessage ?? "",
			Schema = ToStruct(option.Metadata.OptionSchema)
		};

	private static Struct ToStruct(JObject schema) =>
		schema is null
			? null
			: Struct.Parser.ParseJson(schema.ToString(Formatting.None));
}
