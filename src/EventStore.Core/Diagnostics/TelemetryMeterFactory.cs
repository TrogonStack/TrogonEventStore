using System;
using System.Diagnostics.Metrics;
using EventStore.Common.Utils;

namespace EventStore.Core.Diagnostics;

public static class TelemetryMeterFactory
{
	public static Meter Create(string instrumentationScopeName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(instrumentationScopeName);
		return new Meter(instrumentationScopeName, VersionInfo.Version);
	}
}
