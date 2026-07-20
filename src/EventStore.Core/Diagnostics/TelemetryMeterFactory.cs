using System;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace EventStore.Core.Diagnostics;

public static class TelemetryMeterFactory
{
	private static readonly string InstrumentationScopeVersion = GetInstrumentationScopeVersion();

	public static Meter Create(string instrumentationScopeName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(instrumentationScopeName);
		return new Meter(instrumentationScopeName, InstrumentationScopeVersion);
	}

	private static string GetInstrumentationScopeVersion()
	{
		var informationalVersion = typeof(TelemetryMeterFactory).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;
		if (string.IsNullOrWhiteSpace(informationalVersion))
		{
			throw new InvalidOperationException("The telemetry assembly has no informational version.");
		}

		return informationalVersion.Split('+', 2)[0];
	}
}
