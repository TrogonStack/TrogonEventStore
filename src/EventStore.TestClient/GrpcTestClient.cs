using System;
using EventStore.Client;
using ILogger = Serilog.ILogger;

namespace EventStore.TestClient;

/// <summary>
/// A test client that connects using the dotnet gRPC client
/// </summary>
public class GrpcTestClient
{
	private readonly ClientOptions _options;
	private readonly ILogger _log;
	private readonly Func<EventStoreClientSettings> _settingsFactory;

	/// <summary>
	/// Constructs a new <see cref="GrpcTestClient"/>
	/// </summary>
	/// <param name="options"></param>
	/// <param name="log"></param>
	public GrpcTestClient(ClientOptions options, ILogger log)
		: this(options, log, null)
	{
	}

	internal GrpcTestClient(
		ClientOptions options,
		ILogger log,
		Func<EventStoreClientSettings> settingsFactory)
	{
		_options = options;
		_log = log;
		_settingsFactory = settingsFactory;
	}

	/// <summary>
	/// Creates a new gRPC client
	/// </summary>
	/// <returns></returns>
	public EventStoreClient CreateGrpcClient()
		=> CreateGrpcClient(requireLeader: true);

	internal EventStoreClient CreateGrpcClient(bool requireLeader)
	{
		_log.Debug("Creating gRPC client with connection string '{connectionString}'.", ConnectionString);
		return new EventStoreClient(CreateClientSettings(requireLeader));
	}

	internal EventStoreClientSettings CreateClientSettings(bool requireLeader)
	{
		var settings = Settings;
		settings.ConnectivitySettings.NodePreference = requireLeader
			? NodePreference.Leader
			: NodePreference.Random;
		return settings;
	}

	internal EventStoreOperationsClient CreateOperationsClient()
	{
		_log.Debug("Creating gRPC operations client with connection string '{connectionString}'.", ConnectionString);
		return new EventStoreOperationsClient(Settings);
	}

	/// <summary>
	/// True in case username and/or password are not specified.
	/// </summary>
	public bool AreCredentialsMissing =>
		string.IsNullOrWhiteSpace(Settings.DefaultCredentials?.Username) ||
		string.IsNullOrWhiteSpace(Settings.DefaultCredentials?.Password);

	internal EventStoreClientSettings Settings =>
		_settingsFactory?.Invoke() ?? EventStoreClientSettings.Create(ConnectionString);
	internal ClientOptions Options => _options;

	internal Uri HttpEndpoint => new(
		$"{(_options.UseTls ? "https" : "http")}://{_options.Host}:{_options.HttpPort}");

	private string ConnectionString => string.IsNullOrWhiteSpace(_options.ConnectionString)
		? $"esdb://{_options.Host}:{_options.HttpPort}?tls={_options.UseTls}&tlsVerifyCert={_options.TlsValidateServer}"
		: _options.ConnectionString;

}
