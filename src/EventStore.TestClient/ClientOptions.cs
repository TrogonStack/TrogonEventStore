using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using Serilog;

#pragma warning disable 1591

namespace EventStore.TestClient;

/// <summary>
/// Data contract for the command-line options accepted by test client.
/// This contract is handled by CommandLine project for .NET
/// </summary>
public sealed record ClientOptions
{
	public string Host { get; init; }
	public int HttpPort { get; init; }
	public int Timeout { get; init; }
	public string[] Command { get; init; }

	public bool UseTls { get; init; }
	public bool TlsValidateServer { get; init; }

	public string ConnectionString { get; set; }
	public bool OutputCsv { get; set; }
	public ILogger StatsLog { get; set; }

	public ClientOptions()
	{
		Command = Array.Empty<string>();
		Host = IPAddress.Loopback.ToString();
		HttpPort = 2113;
		Timeout = -1;
		UseTls = false;
		TlsValidateServer = false;
		ConnectionString = string.Empty;
		OutputCsv = true;
	}

	public override string ToString()
	{
		return GetType()
			.GetProperties()
			.Aggregate(new StringBuilder(),
				(builder, option) => builder.AppendLine($"{option.Name}: {GetValue(option)}"))
			.ToString();

		object GetValue(PropertyInfo propertyInfo) => propertyInfo.PropertyType.IsArray
			? string.Join(",", ((IEnumerable)propertyInfo.GetValue(this)).OfType<object>())
			: propertyInfo.GetValue(this);
	}
}
