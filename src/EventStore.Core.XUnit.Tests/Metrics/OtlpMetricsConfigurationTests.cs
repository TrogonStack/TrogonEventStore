using System.Collections.Generic;
using EventStore.Common.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class OtlpMetricsConfigurationTests
{
	[Fact]
	public void IsDisabledByDefault()
	{
		var configuration = new ConfigurationBuilder().Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.Otlp.Enabled.Should().BeFalse();
	}

	[Fact]
	public void BindsOtlpSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:Metrics:Otlp:Enabled"] = "true",
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.Otlp.Enabled.Should().BeTrue();
	}
}
