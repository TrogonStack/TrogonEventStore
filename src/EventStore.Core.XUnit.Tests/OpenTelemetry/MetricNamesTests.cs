using System;
using System.Linq;
using System.Reflection;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class MetricNamesTests
{
	[Fact]
	public void all_contains_every_metric_constant_once_in_name_order()
	{
		var constants = typeof(MetricNames)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.IsLiteral && field.FieldType == typeof(string))
			.Select(field => (string)field.GetRawConstantValue())
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(constants, MetricNames.All);
		Assert.Equal(constants.Length, constants.Distinct(StringComparer.Ordinal).Count());
	}
}
