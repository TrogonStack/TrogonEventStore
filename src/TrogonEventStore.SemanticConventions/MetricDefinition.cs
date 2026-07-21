using System;

namespace TrogonEventStore.SemanticConventions
{
	public sealed class MetricDefinition : IEquatable<MetricDefinition>
	{
		public MetricDefinition(
			string name,
			string unit,
			string description,
			MetricInstrumentKind instrumentKind)
		{
			Name = RequireValue(name, nameof(name));
			Unit = RequireValue(unit, nameof(unit));
			Description = RequireValue(description, nameof(description));
			if (!Enum.IsDefined(typeof(MetricInstrumentKind), instrumentKind))
			{
				throw new ArgumentOutOfRangeException(nameof(instrumentKind), instrumentKind, "Unknown metric instrument kind.");
			}

			InstrumentKind = instrumentKind;
		}

		public string Name { get; }
		public string Unit { get; }
		public string Description { get; }
		public MetricInstrumentKind InstrumentKind { get; }

		public bool Equals(MetricDefinition other) =>
			other is not null &&
			StringComparer.Ordinal.Equals(Name, other.Name) &&
			StringComparer.Ordinal.Equals(Unit, other.Unit) &&
			StringComparer.Ordinal.Equals(Description, other.Description) &&
			InstrumentKind == other.InstrumentKind;

		public override bool Equals(object obj) => Equals(obj as MetricDefinition);

		public override int GetHashCode()
		{
			unchecked
			{
				var hashCode = StringComparer.Ordinal.GetHashCode(Name);
				hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(Unit);
				hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(Description);
				hashCode = (hashCode * 397) ^ (int)InstrumentKind;
				return hashCode;
			}
		}

		public override string ToString() => Name;

		private static string RequireValue(string value, string parameterName)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				throw new ArgumentException("Metric metadata cannot be empty.", parameterName);
			}

			return value;
		}
	}
}
