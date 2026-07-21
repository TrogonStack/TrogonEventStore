using System;
using System.Collections.Generic;
using System.Reflection;

namespace EventStore.Core.Diagnostics;

public static class TelemetryMeterInstrumentation
{
	public const string CoreName = "EventStore.Core";
	public const string DotNetRuntimeName = "System.Runtime";
	public const string KestrelName = "Microsoft.AspNetCore.Server.Kestrel";
	public const string ProjectionsName = "EventStore.Projections.Core";

	public static string ScopeVersion { get; } = GetScopeVersion();

	public static string[] GetNames(IEnumerable<string> additionalMeterNames)
	{
		ArgumentNullException.ThrowIfNull(additionalMeterNames);

		var names = new List<string> { CoreName, ProjectionsName, DotNetRuntimeName, KestrelName };
		var seen = new HashSet<string>(names, StringComparer.Ordinal);

		foreach (var name in additionalMeterNames)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(name);
			if (seen.Add(name))
			{
				names.Add(name);
			}
		}

		return names.ToArray();
	}

	private static string GetScopeVersion()
	{
		var informationalVersion = typeof(TelemetryMeterInstrumentation).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;
		if (string.IsNullOrWhiteSpace(informationalVersion))
		{
			throw new InvalidOperationException("The telemetry assembly has no informational version.");
		}

		return informationalVersion.Split('+', 2)[0];
	}
}
