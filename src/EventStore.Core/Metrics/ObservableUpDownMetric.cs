using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics;

public class ObservableUpDownMetric<T> where T : struct
{
	private readonly List<Func<Measurement<T>>> _measurementProviders = new();
	private readonly object _lock = new();

	public ObservableUpDownMetric(Meter meter, MetricDefinition definition)
	{
		definition.EnsureInstrumentKind(MetricInstrumentKind.UpDownCounter);
		meter.CreateObservableUpDownCounter(
			definition.Name,
			Observe,
			definition.Unit,
			definition.Description);
	}

	public void Register(Func<Measurement<T>> measurementProvider)
	{
		if (measurementProvider is null)
		{
			throw new ArgumentException("Measurement provider couldn't be null");
		}

		lock (_lock)
		{
			_measurementProviders.Add(measurementProvider);
		}
	}

	private IEnumerable<Measurement<T>> Observe()
	{
		lock (_lock)
		{
			foreach (var measurementProvider in _measurementProviders)
			{
				yield return measurementProvider();
			}
		}
	}
}

internal static class MetricDefinitionExtensions
{
	public static void EnsureInstrumentKind(
		this MetricDefinition definition,
		MetricInstrumentKind expected)
	{
		ArgumentNullException.ThrowIfNull(definition);
		if (definition.InstrumentKind != expected)
		{
			throw new ArgumentException(
				$"Metric '{definition.Name}' requires a {expected} instrument.",
				nameof(definition));
		}
	}
}
