using System;
using System.Diagnostics.Metrics;

namespace EventStore.Core.Diagnostics;

public static class TelemetryMeterFactory
{
	public static Meter Create(string instrumentationScopeName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(instrumentationScopeName);
		return new Meter(instrumentationScopeName, TelemetryMeterInstrumentation.ScopeVersion);
	}
}
