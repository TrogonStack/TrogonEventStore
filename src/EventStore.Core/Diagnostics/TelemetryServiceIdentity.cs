using System;
using System.Collections.Generic;
using EventStore.Common.Utils;
using OpenTelemetry.Resources;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Diagnostics;

public sealed record TelemetryServiceIdentity
{
	private const string DefaultServiceName = "eventstore";

	public string ServiceName { get; }
	public string ServiceInstanceId { get; }
	public string ServiceVersion { get; }

	private TelemetryServiceIdentity(string name, string instanceId, string version)
	{
		ServiceName = name;
		ServiceInstanceId = instanceId;
		ServiceVersion = version;
	}

	public static TelemetryServiceIdentity ForComponent(string componentName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
		return new TelemetryServiceIdentity(DefaultServiceName, componentName, VersionInfo.Version);
	}

	public Dictionary<string, object> CreateAttributeDictionary() =>
		new(GetAttributes());

	public ResourceBuilder CreateResourceBuilder() =>
		ResourceBuilder.CreateDefault().AddAttributes(GetAttributes());

	private IEnumerable<KeyValuePair<string, object>> GetAttributes()
	{
		yield return new KeyValuePair<string, object>(AttributeNames.ServiceName, ServiceName);
		yield return new KeyValuePair<string, object>(AttributeNames.ServiceInstanceId, ServiceInstanceId);
		yield return new KeyValuePair<string, object>(AttributeNames.ServiceVersion, ServiceVersion);
	}
}
