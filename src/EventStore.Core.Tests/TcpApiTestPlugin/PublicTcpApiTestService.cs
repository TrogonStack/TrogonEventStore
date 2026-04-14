#nullable enable

using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Bus;
using EventStore.Core.Certificates;
using EventStore.Core.Messages;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Plugins.Authentication;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace EventStore.TcpUnitTestPlugin;

public class PublicTcpApiTestService : IHostedService
{
	static readonly ILogger Logger = Log.ForContext<PublicTcpApiTestService>();
	private readonly TcpService _tcpService;
	private int _initialized;
	private int _started;

	PublicTcpApiTestService(TcpService tcpService, ISubscriber bus)
	{
		_tcpService = tcpService;

		bus.Subscribe<SystemMessage.SystemInit>(new AdHocHandler<SystemMessage.SystemInit>(_ => InitializeTcpService()));
		bus.Subscribe<SystemMessage.SystemStart>(new AdHocHandler<SystemMessage.SystemStart>(_ => StartTcpService()));
		bus.Subscribe<SystemMessage.BecomeShuttingDown>(tcpService);

		_ = Task.Run(async () =>
		{
			await Task.Delay(TimeSpan.FromHours(1));
			Logger.Warning("Shutting down TCP unit tests");
			tcpService.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), true, true));
		});
	}

	public static PublicTcpApiTestService Insecure(
		TcpApiTestOptions options,
		IAuthenticationProvider authProvider,
		AuthorizationGateway authGateway,
		StandardComponents components
	)
	{
		var endpoint = new IPEndPoint(IPAddress.Loopback, options.NodeTcpPort);

		var tcpService = new TcpService(
			publisher: components.MainQueue,
			serverEndPoint: endpoint,
			networkSendQueue: components.NetworkSendService,
			serviceType: TcpServiceType.External, securityType: TcpSecurityType.Normal,
			dispatcher: new ClientTcpDispatcher(options.WriteTimeoutMs),
			heartbeatInterval: TimeSpan.FromMilliseconds(options.NodeHeartbeatInterval),
			heartbeatTimeout: TimeSpan.FromMilliseconds(options.NodeHeartbeatTimeout),
			authProvider: authProvider,
			authorizationGateway: authGateway,
			certificateSelector: null,
			intermediatesSelector: null,
			sslClientCertValidator: null,
			connectionPendingSendBytesThreshold: options.ConnectionPendingSendBytesThreshold,
			connectionQueueSizeThreshold: options.ConnectionQueueSizeThreshold
		);

		return new(tcpService, components.MainBus);
	}

	public static PublicTcpApiTestService Secure(
		TcpApiTestOptions options,
		IAuthenticationProvider authProvider,
		AuthorizationGateway authGateway,
		StandardComponents components,
		CertificateProvider? certificateProvider
	)
	{
		var endpoint = new IPEndPoint(IPAddress.Loopback, options.NodeTcpPort);

		var tcpService = new TcpService(
			publisher: components.MainQueue,
			serverEndPoint: endpoint,
			networkSendQueue: components.NetworkSendService,
			serviceType: TcpServiceType.External, securityType: TcpSecurityType.Secure,
			dispatcher: new ClientTcpDispatcher(options.WriteTimeoutMs),
			heartbeatInterval: TimeSpan.FromMilliseconds(options.NodeHeartbeatInterval),
			heartbeatTimeout: TimeSpan.FromMilliseconds(options.NodeHeartbeatTimeout),
			authProvider: authProvider,
			authorizationGateway: authGateway,
			certificateSelector: () => certificateProvider?.Certificate,
			intermediatesSelector: () =>
			{
				var intermediates = certificateProvider?.IntermediateCerts;
				return intermediates == null ? null : new X509Certificate2Collection(intermediates);
			},
			sslClientCertValidator: delegate
			{ return (true, null); },
			connectionPendingSendBytesThreshold: options.ConnectionPendingSendBytesThreshold,
			connectionQueueSizeThreshold: options.ConnectionQueueSizeThreshold
		);

		return new(tcpService, components.MainBus);
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
				InitializeTcpService();
				StartTcpService();
			}
			catch (OperationCanceledException)
			{
			}
		}, CancellationToken.None);

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	private void InitializeTcpService()
	{
		if (Interlocked.Exchange(ref _initialized, 1) == 1)
			return;

		_tcpService.Handle(new SystemMessage.SystemInit());
	}

	private void StartTcpService()
	{
		InitializeTcpService();

		if (Interlocked.Exchange(ref _started, 1) == 1)
			return;

		_tcpService.Handle(new SystemMessage.SystemStart());
	}
}
