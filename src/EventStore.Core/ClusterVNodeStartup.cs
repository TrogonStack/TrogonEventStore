using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Common.Configuration;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Configuration;
using EventStore.Core.Diagnostics;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Cluster;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Plugins;
using EventStore.Plugins.Authentication;
using EventStore.Plugins.Authorization;
using Grpc.Net.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ClientGossip = EventStore.Core.Services.Transport.Grpc.Gossip;
using ClusterGossip = EventStore.Core.Services.Transport.Grpc.Cluster.Gossip;
using GrpcOperations = EventStore.Core.Services.Transport.Grpc.Operations;
using ServerFeatures = EventStore.Core.Services.Transport.Grpc.ServerFeatures;

#nullable enable
namespace EventStore.Core;

public class ClusterVNodeStartup<TStreamId> : IInternalStartup, IHandle<SystemMessage.SystemReady>,
	IHandle<SystemMessage.BecomeShuttingDown>
{

	private readonly IReadOnlyList<IPlugableComponent> _plugableComponents;
	private readonly IPublisher _mainQueue;
	private readonly IPublisher _monitoringQueue;
	private readonly ISubscriber _mainBus;
	private readonly IAuthenticationProvider _authenticationProvider;
	private readonly int _maxAppendSize;
	private readonly TimeSpan _writeTimeout;
	private readonly IExpiryStrategy _expiryStrategy;
	private readonly IConfiguration _configuration;
	private readonly Trackers _trackers;
	private readonly TelemetryServiceIdentity _telemetryServiceIdentity;
	private readonly NodeHealthState _nodeHealthState;
	private readonly NodeInformationProvider _nodeInformationProvider;
	private readonly bool _disableHttpMetrics;
	private readonly Func<IServiceCollection, IServiceCollection> _configureNodeServices;
	private readonly Action<IApplicationBuilder> _configureNode;

	private readonly IAuthorizationProvider _authorizationProvider;
	private readonly string _clusterDns;

	public ClusterVNodeStartup(
		IReadOnlyList<IPlugableComponent> plugableComponents,
		IPublisher mainQueue,
		IPublisher monitoringQueue,
		ISubscriber mainBus,
		IAuthenticationProvider authenticationProvider,
		IAuthorizationProvider authorizationProvider,
		int maxAppendSize,
		TimeSpan writeTimeout,
		IExpiryStrategy expiryStrategy,
		bool disableHttpMetrics,
		IConfiguration configuration,
		Trackers trackers,
		TelemetryServiceIdentity telemetryServiceIdentity,
		NodeInformationProvider nodeInformationProvider,
		string clusterDns,
		Func<IServiceCollection, IServiceCollection> configureNodeServices,
		Action<IApplicationBuilder> configureNode)
	{

		Ensure.Positive(maxAppendSize, nameof(maxAppendSize));

		ArgumentNullException.ThrowIfNull(configuration);

		if (mainBus == null)
		{
			throw new ArgumentNullException(nameof(mainBus));
		}

		if (monitoringQueue == null)
		{
			throw new ArgumentNullException(nameof(monitoringQueue));
		}

		_plugableComponents = plugableComponents;
		_mainQueue = mainQueue;
		_monitoringQueue = monitoringQueue;
		_mainBus = mainBus;
		_authenticationProvider = authenticationProvider;
		_authorizationProvider =
			authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider));
		_maxAppendSize = maxAppendSize;
		_writeTimeout = writeTimeout;
		_expiryStrategy = expiryStrategy;
		_disableHttpMetrics = disableHttpMetrics;
		_configuration = configuration;
		_trackers = trackers;
		_telemetryServiceIdentity = telemetryServiceIdentity ??
			throw new ArgumentNullException(nameof(telemetryServiceIdentity));
		_nodeInformationProvider =
			nodeInformationProvider ?? throw new ArgumentNullException(nameof(nodeInformationProvider));
		_clusterDns = clusterDns;
		_configureNodeServices =
			configureNodeServices ?? throw new ArgumentNullException(nameof(configureNodeServices));
		_configureNode = configureNode ?? throw new ArgumentNullException(nameof(configureNode));
		_nodeHealthState = new NodeHealthState();
	}

	public void Configure(IApplicationBuilder app)
	{
		_configureNode(app);

		app = app
			.UseCors("default")
			// AuthenticationMiddleware uses _httpAuthenticationProviders and assigns
			// the resulting ClaimsPrinciple to HttpContext.User
			.UseMiddleware<AuthenticationMiddleware>()

			// UseAuthentication/UseAuthorization allow the rest of the pipeline to access auth
			// in a conventional way (e.g. with AuthorizeAttribute). The server doesn't make use
			// of this yet but plugins may. The registered authentication scheme (es auth)
			// is driven by the HttpContext.User established above
			.UseAuthentication()
			.UseRouting()
			.UseMiddleware<GrpcStreamLifetimeMiddleware>()
			.UseAuthorization()
			.UseAntiforgery();

		if (!_disableHttpMetrics)
		{
			app.Map("/-/metrics", metrics => metrics
				.UseOpenTelemetryPrometheusScrapingEndpoint(static _ => true));
		}

		foreach (var component in _plugableComponents)
		{
			component.ConfigureApplication(app, _configuration);
		}

		app.UseEndpoints(ep =>
		{
			ep.MapHealthChecks("/-/liveness", new HealthCheckOptions
			{
				Predicate = registration => registration.Tags.Contains("liveness")
			});
			ep.MapHealthChecks("/-/readiness", new HealthCheckOptions
			{
				Predicate = registration => registration.Tags.Contains("readiness")
			});

			_authenticationProvider.ConfigureEndpoints(ep);
			ep.MapGrpcHealthChecksService();
			ep.MapGrpcService<PersistentSubscriptions>();
			ep.MapGrpcService<Users>();
			ep.MapGrpcService<Streams<TStreamId>>();
			ep.MapGrpcService<ClusterGossip>();
			ep.MapGrpcService<Elections>();
			ep.MapGrpcService<GrpcOperations>();
			ep.MapGrpcService<ClientGossip>();
			ep.MapGrpcService<Monitoring>();
			ep.MapGrpcService<NodeInformation>();
			ep.MapGrpcService<ServerFeatures>();

			// enable redaction service on unix sockets only
			ep.MapGrpcService<Redaction>().AddEndpointFilter(async (c, next) =>
			{
				if (!c.HttpContext.IsUnixSocketConnection())
				{
					return Results.BadRequest("Redaction is only available via Unix Sockets");
				}

				return await next(c).ConfigureAwait(false);
			});
		});
	}

	public IServiceProvider ConfigureServices(IServiceCollection services) =>
		throw new NotSupportedException(
			$"{nameof(ClusterVNodeStartup<TStreamId>)} is driven via {nameof(IInternalStartup)}.{nameof(ConfigureServicesOnly)} " +
			$"and the host's service provider. Use {nameof(ConfigureServicesOnly)} instead.");

	public void ConfigureServicesOnly(IServiceCollection services)
	{
		var metricsConfiguration = MetricsConfiguration.Get(_configuration);

		services = services
			.AddRouting()
			.AddAuthentication(o => o
				.AddScheme<EventStoreAuthenticationHandler>("es auth", displayName: null))
			.Services
			.AddAuthorization()
			.AddAntiforgery()
			.AddSingleton(_authenticationProvider)
			.AddSingleton(_authorizationProvider)
			.AddSingleton(_nodeHealthState)
			.AddSingleton<ISubscriber>(_mainBus)
			.AddSingleton<IPublisher>(_mainQueue)
			.AddSingleton<AuthenticationMiddleware>()
			.AddSingleton(new Streams<TStreamId>(_mainQueue, _maxAppendSize,
				_writeTimeout, _expiryStrategy,
				_trackers.GrpcTrackers,
				_authorizationProvider))
			.AddSingleton(new PersistentSubscriptions(_mainQueue, _authorizationProvider))
			.AddSingleton(new Users(_mainQueue, _authorizationProvider))
				.AddSingleton(new GrpcOperations(_mainQueue, _authorizationProvider))
			.AddSingleton(new ClusterGossip(_mainQueue, _authorizationProvider, _clusterDns,
				updateTracker: _trackers.GossipTrackers.ProcessingPushFromPeer,
				readTracker: _trackers.GossipTrackers.ProcessingRequestFromPeer))
			.AddSingleton(new Elections(_mainQueue, _authorizationProvider, _clusterDns))
			.AddSingleton(new ClientGossip(_mainQueue, _authorizationProvider,
				_trackers.GossipTrackers.ProcessingRequestFromGrpcClient))
			.AddSingleton(new Monitoring(_monitoringQueue))
			.AddSingleton(_nodeInformationProvider)
			.AddSingleton(new NodeInformation(_nodeInformationProvider, _authorizationProvider))
			.AddSingleton(new Redaction(_mainQueue, _authorizationProvider))
			.AddSingleton<ServerFeatures>()

			.AddOpenTelemetry()
			.WithMetrics(meterOptions => ConfigureMetrics(
				meterOptions,
				metricsConfiguration,
				_configuration,
				_telemetryServiceIdentity))
			.WithTracing(tracerOptions => ConfigureTracing(
				tracerOptions,
				_configuration,
				_telemetryServiceIdentity))
			.Services
			.AddGrpcHealthChecks(options =>
			{
				options.Services.Map("", registration => registration.Tags.Contains("readiness"));
				options.Services.Map("readiness", registration => registration.Tags.Contains("readiness"));
				options.Services.Map("liveness", registration => registration.Tags.Contains("liveness"));
			})
			.Services

			// gRPC
			.AddSingleton<RetryInterceptor>()
			.AddSingleton<GrpcServerShutdownInterceptor>()
			.AddGrpc(options =>
			{
				options.Interceptors.Add<RetryInterceptor>();
				options.Interceptors.Add<GrpcServerShutdownInterceptor>();

				var eventStoreConfiguration = _configuration.GetSection("EventStore");
				var compressionLevel = (eventStoreConfiguration.Exists()
						? eventStoreConfiguration
						: _configuration)
					.BindOptions<ClusterVNodeOptions.GrpcOptions>()
					.CompressionLevel;
				options.CompressionProviders.Add(new GzipCompressionProvider(compressionLevel));
				if (compressionLevel != CompressionLevel.NoCompression)
				{
					options.ResponseCompressionAlgorithm = "gzip";
					options.ResponseCompressionLevel = compressionLevel;
				}
			})
			.AddServiceOptions<Streams<TStreamId>>(options =>
				options.MaxReceiveMessageSize = TFConsts.EffectiveMaxLogRecordSize)
			.Services
			.AddHealthChecks()
			.AddCheck("node-liveness",
				() => HealthCheckResult.Healthy(),
				tags: ["liveness"])
			.AddCheck("node-readiness",
				() => _nodeHealthState.IsReady ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(),
				tags: ["readiness"])
			.Services;

		services.AddCors(o => o.AddPolicy(
			"default",
			b => b.AllowAnyOrigin().WithMethods(HttpMethods.Options, HttpMethods.Get).AllowAnyHeader())
		);

		services = _configureNodeServices(services);

		foreach (var component in _plugableComponents)
		{
			component.ConfigureServices(services, _configuration);
		}
	}

	private static void ConfigureMetrics(
		MeterProviderBuilder meterOptions,
		MetricsConfiguration metricsConfiguration,
		IConfiguration configuration,
		TelemetryServiceIdentity serviceIdentity)
	{
		meterOptions
			.SetResourceBuilder(serviceIdentity.CreateResourceBuilder())
			.AddMeter(TelemetryMeterInstrumentation.GetNames(metricsConfiguration.Meters))
			.AddView(i =>
			{
				if (i.Name == MetricsBootstrapper.LogicalChunkReadDistributionName)
				{
					// 20 buckets, 0, 1, 2, 4, 8, ...
					return new ExplicitBucketHistogramConfiguration
					{
						Boundaries =
						[
							0,
							.. Enumerable.Range(0, count: 19).Select(x => 1 << x)
						]
					};
				}
				else if (i.Name.StartsWith("eventstore-") &&
						 i.Name.EndsWith("-latency-seconds"))
				{
					return new ExplicitBucketHistogramConfiguration
					{
						Boundaries =
						[
							0.001, //    1 ms
							0.005, //    5 ms
							0.01, //   10 ms
							0.05, //   50 ms
							0.1, //  100 ms
							0.5, //  500 ms
							1, // 1000 ms
							5, // 5000 ms
						]
					};
				}
				else if (i.Name.StartsWith("eventstore-") &&
						 i.Name.EndsWith("-seconds"))
				{
					return new ExplicitBucketHistogramConfiguration
					{
						Boundaries =
						[
							0.000_001, // 1 microsecond
							0.000_01,
							0.000_1,
							0.001, // 1 millisecond
							0.01,
							0.1,
							1, // 1 second
							10,
						]
					};
				}

				return default;
			})
			.AddPrometheusExporter(options => options.ScrapeResponseCacheDurationMilliseconds = 1000);

		ConfigureOtlpMetrics(meterOptions, metricsConfiguration, configuration);
	}

	private static void ConfigureOtlpMetrics(
		MeterProviderBuilder meterOptions,
		MetricsConfiguration metricsConfiguration,
		IConfiguration configuration)
	{
		if (!configuration.OtlpMetricsEnabled(metricsConfiguration))
		{
			return;
		}

		meterOptions.AddOtlpExporter((exporterOptions, metricReaderOptions) =>
		{
			configuration.BindOtlpExporterOptions(
				OpenTelemetryConfiguration.OtlpMetricsOtlpPrefix,
				exporterOptions);
			configuration.GetSection(OpenTelemetryConfiguration.OtlpMetricsPrefix).Bind(metricReaderOptions);

			var periodicOptions = metricReaderOptions.PeriodicExportingMetricReaderOptions;
			if (periodicOptions.ExportIntervalMilliseconds is null &&
				metricsConfiguration.ExpectedScrapeIntervalSeconds > 0)
			{
				periodicOptions.ExportIntervalMilliseconds =
					metricsConfiguration.ExpectedScrapeIntervalSeconds * 1000;
			}
		});
	}

	private static void ConfigureTracing(
		TracerProviderBuilder tracerOptions,
		IConfiguration configuration,
		TelemetryServiceIdentity serviceIdentity)
	{
		tracerOptions.SetResourceBuilder(serviceIdentity.CreateResourceBuilder());

		if (!configuration.OtlpTracesEnabled())
		{
			return;
		}

		tracerOptions
			.AddAspNetCoreInstrumentation()
			.AddHttpClientInstrumentation()
			.AddOtlpExporter(exporterOptions => configuration.BindOtlpExporterOptions(
				OpenTelemetryConfiguration.OtlpTracesOtlpPrefix,
				exporterOptions));
	}

	public void Handle(SystemMessage.SystemReady _) => _nodeHealthState.MarkReady();

	public void Handle(SystemMessage.BecomeShuttingDown _) => _nodeHealthState.MarkShuttingDown();

	private sealed class NodeHealthState
	{
		public bool IsReady { get; private set; }

		public void MarkReady()
		{
			IsReady = true;
		}

		public void MarkShuttingDown()
		{
			IsReady = false;
		}
	}
}
