using System;
using System.Collections.Generic;
using EventStore.Common.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class MetricsConfigurationTests
{
	[Fact]
	public void UsesDefaultSlowMessageThresholdWhenNameIsNotConfigured()
	{
		var configuration = new ConfigurationBuilder().Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.GetSlowMessageThreshold("MainBus").Should().Be(MetricsConfiguration.DefaultSlowMessageThreshold);
	}

	[Fact]
	public void UsesFallbackSlowMessageThresholdWhenNameIsNotConfigured()
	{
		var configuration = new ConfigurationBuilder().Build();
		var fallback = TimeSpan.FromMilliseconds(500);

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.GetSlowMessageThreshold("StorageWriterQueue", fallback).Should().Be(fallback);
	}

	[Fact]
	public void BindsNamedSlowMessageThreshold()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:Metrics:SlowMessageThresholdMilliseconds:StorageReaderBus"] = "200",
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.GetSlowMessageThreshold("StorageReaderBus").Should().Be(TimeSpan.FromMilliseconds(200));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void DisablesSlowMessageThresholdWhenConfiguredAsNonPositive(int thresholdMilliseconds)
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:Metrics:SlowMessageThresholdMilliseconds:MonitoringRequestBus"] =
					thresholdMilliseconds.ToString(),
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.GetSlowMessageThreshold("MonitoringRequestBus").Should().Be(TimeSpan.Zero);
	}
}
