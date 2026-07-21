using System;
using System.Linq;
using System.Reflection;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class MetricNamesTests
{
	[Fact]
	public void all_contains_every_metric_definition_once_in_name_order()
	{
		var definitions = typeof(MetricDefinitions)
			.GetProperties(BindingFlags.Public | BindingFlags.Static)
			.Where(property => property.PropertyType == typeof(MetricDefinition))
			.Select(property => (MetricDefinition)property.GetValue(null))
			.OrderBy(definition => definition.Name, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(definitions, MetricDefinitions.All);
		Assert.Equal(definitions.Length, definitions.Distinct().Count());
	}

	[Fact]
	public void every_metric_definition_has_complete_metadata()
	{
		foreach (var definition in MetricDefinitions.All)
		{
			Assert.False(string.IsNullOrWhiteSpace(definition.Name));
			Assert.False(string.IsNullOrWhiteSpace(definition.Unit));
			Assert.False(string.IsNullOrWhiteSpace(definition.Description));
			Assert.True(Enum.IsDefined(typeof(MetricInstrumentKind), definition.InstrumentKind));
		}
	}

	[Fact]
	public void metric_definition_contains_weaver_metadata()
	{
		Assert.Equal(
			new MetricDefinition(
				"trogon.eventstore.grpc.server.call.duration",
				"s",
				"Duration of configured gRPC server calls.",
				MetricInstrumentKind.Histogram),
			MetricDefinitions.TrogonEventstoreGrpcServerCallDuration);
	}

	[Fact]
	public void official_catalog_contains_only_selected_metrics_in_name_order()
	{
		var definitions = typeof(OpenTelemetryMetricDefinitions)
			.GetProperties(BindingFlags.Public | BindingFlags.Static)
			.Where(property => property.PropertyType == typeof(MetricDefinition))
			.Select(property => (MetricDefinition)property.GetValue(null))
			.OrderBy(definition => definition.Name, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(definitions, OpenTelemetryMetricDefinitions.All);
		Assert.Equal(
			new[]
			{
				"process.disk.io",
				"process.uptime",
				"system.filesystem.limit",
				"system.filesystem.usage",
			},
			OpenTelemetryMetricDefinitions.All.Select(definition => definition.Name));
	}

	[Fact]
	public void trogon_attribute_catalog_contains_all_custom_attributes_in_name_order()
	{
		var constants = typeof(TrogonAttributeNames)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.IsLiteral && field.FieldType == typeof(string))
			.Select(field => (string)field.GetRawConstantValue())
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(
			new[]
			{
				"trogon.eventstore.activity.name",
				"trogon.eventstore.activity.outcome",
				"trogon.eventstore.cache.name",
				"trogon.eventstore.cache.resource",
				"trogon.eventstore.cache.result",
				"trogon.eventstore.checkpoint.name",
				"trogon.eventstore.checkpoint.read_kind",
				"trogon.eventstore.component.name",
				"trogon.eventstore.component.status",
				"trogon.eventstore.measurement.window",
				"trogon.eventstore.persistent_subscription.group",
				"trogon.eventstore.persistent_subscription.reason",
				"trogon.eventstore.persistent_subscription.stream",
				"trogon.eventstore.projection.name",
				"trogon.eventstore.projection.status",
				"trogon.eventstore.queue.message.type",
				"trogon.eventstore.queue.name",
				"trogon.eventstore.storage.activity",
				"trogon.eventstore.storage.source",
				"trogon.eventstore.system.load_average.period",
			},
			TrogonAttributeNames.All);
		Assert.Equal(constants, TrogonAttributeNames.All);
	}

	[Fact]
	public void official_attribute_catalog_contains_only_selected_attributes()
	{
		var constants = typeof(AttributeNames)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.IsLiteral && field.FieldType == typeof(string))
			.Select(field => (string)field.GetRawConstantValue())
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(
			new[]
			{
				"disk.io.direction",
				"host.arch",
				"host.name",
				"process.creation.time",
				"process.executable.name",
				"process.pid",
				"process.runtime.name",
				"process.runtime.version",
				"service.instance.id",
				"service.name",
				"service.version",
				"system.filesystem.mountpoint",
				"system.filesystem.state",
			},
			constants);
	}

	[Fact]
	public void metric_definition_has_value_semantics()
	{
		var expected = new MetricDefinition("metric", "s", "Description.", MetricInstrumentKind.Histogram);
		var actual = new MetricDefinition("metric", "s", "Description.", MetricInstrumentKind.Histogram);

		Assert.Equal(expected, actual);
		Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
		Assert.Equal("metric", actual.ToString());
	}

	[Fact]
	public void metric_definition_rejects_invalid_metadata()
	{
		Assert.Throws<ArgumentException>(() =>
			new MetricDefinition("", "s", "Description.", MetricInstrumentKind.Histogram));
		Assert.Throws<ArgumentException>(() =>
			new MetricDefinition("metric", "", "Description.", MetricInstrumentKind.Histogram));
		Assert.Throws<ArgumentException>(() =>
			new MetricDefinition("metric", "s", "", MetricInstrumentKind.Histogram));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new MetricDefinition("metric", "s", "Description.", (MetricInstrumentKind)(-1)));
	}
}
