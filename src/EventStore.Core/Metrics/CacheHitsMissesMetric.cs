using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using TrogonEventStore.SemanticConventions;
using static EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core.Metrics;

public class CacheHitsMissesMetric
{
	private readonly Dictionary<Cache, string> _enabledCaches;
	private readonly List<Func<long>> _funcs = new();
	private readonly List<KeyValuePair<string, object>[]> _tagss = new();
	private readonly object _lock = new();

	public CacheHitsMissesMetric(
		Meter meter,
		Cache[] enabledCaches,
		MetricDefinition definition,
		Dictionary<Cache, string> cacheNames)
	{

		definition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
		_enabledCaches = new(cacheNames.Where(x => enabledCaches.Contains(x.Key)));
		meter.CreateObservableCounter(
			definition.Name,
			Observe,
			definition.Unit,
			definition.Description);
	}

	public void Register(Cache cache, Func<long> getHits, Func<long> getMisses)
	{
		Register(cache, "hit", getHits);
		Register(cache, "miss", getMisses);
	}

	private void Register(Cache cache, string kind, Func<long> func)
	{
		if (!_enabledCaches.TryGetValue(cache, out var cacheName))
		{
			return;
		}

		lock (_lock)
		{
			_funcs.Add(func);
			_tagss.Add(new KeyValuePair<string, object>[] {
				new(TrogonAttributeNames.CacheName, cacheName),
				new(TrogonAttributeNames.CacheResult, kind),
			});
		}
	}

	private IEnumerable<Measurement<long>> Observe()
	{
		lock (_lock)
		{
			for (var i = 0; i < _funcs.Count; i++)
			{
				yield return new(_funcs[i](), _tagss[i]);
			}
		}
	}
}
