using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace EventStore.Common.Configuration;

public sealed class SectionProvider : ConfigurationProvider, IDisposable
{
	private readonly IConfigurationRoot _configuration;
	public IEnumerable<IConfigurationProvider> Providers => _configuration.Providers;
	private readonly string _sectionName;
	private readonly IDisposable _registration;

	public SectionProvider(string sectionName, IConfigurationRoot configuration)
	{
		_configuration = configuration;
		_sectionName = sectionName;
		_registration = ChangeToken.OnChange(
			configuration.GetReloadToken,
			Load);
	}

	public bool TryGetProviderFor(string key, out IConfigurationProvider provider)
	{
		var prefix = _sectionName + ":";
		if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			key = key[prefix.Length..];
			foreach (var candidate in Providers)
			{
				if (!candidate.TryGet(key, out _)) continue;
				provider = candidate;
				return true;
			}
		}

		provider = null;
		return false;
	}

	public void Dispose() => _registration.Dispose();

	public override void Load()
	{
		var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var kvp in _configuration.AsEnumerable())
		{
			data[_sectionName + ":" + kvp.Key] = kvp.Value;
		}

		Data = data;
		OnReload();
	}
}
