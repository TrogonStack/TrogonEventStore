using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using EventStore.Common.Utils;
using OpenTelemetry.Resources;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Diagnostics;

public sealed record TelemetryServiceIdentity
{
	private const string DefaultServiceName = "eventstore";
	private static readonly string ProcessCreationTime = GetProcessCreationTime();

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
		yield return new KeyValuePair<string, object>(AttributeNames.ProcessCreationTime, ProcessCreationTime);
		yield return new KeyValuePair<string, object>(AttributeNames.ProcessPid, Environment.ProcessId);
		yield return new KeyValuePair<string, object>(
			AttributeNames.ProcessExecutableName,
			Path.GetFileName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.FriendlyName);
		yield return new KeyValuePair<string, object>(AttributeNames.ProcessRuntimeName, ".NET");
		yield return new KeyValuePair<string, object>(AttributeNames.ProcessRuntimeVersion, Environment.Version.ToString());
		yield return new KeyValuePair<string, object>(AttributeNames.HostName, Environment.MachineName);
		yield return new KeyValuePair<string, object>(AttributeNames.HostArch, GetHostArchitecture());
	}

	private static string GetProcessCreationTime()
	{
		using var process = Process.GetCurrentProcess();
		return process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
	}

	private static string GetHostArchitecture() =>
		RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "amd64",
			Architecture.X86 => "x86",
			Architecture.Arm => "arm32",
			Architecture.Arm64 => "arm64",
			var architecture => architecture.ToString().ToLowerInvariant(),
		};
}
