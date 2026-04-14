using System;
using System.Net;
using System.Threading;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Internal;
using EventStore.ClientAPI.SystemData;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.ClientAPI.Helpers;

public static class TestConnection
{
	private static int _nextConnId = -1;

	public static IEventStoreConnection Create(IPEndPoint endPoint, TcpType tcpType = TcpType.Ssl,
		UserCredentials userCredentials = null)
	{

		return EventStoreConnection.Create(Settings(tcpType, userCredentials),
			endPoint.ToESTcpUri(),
			$"ESC-{Interlocked.Increment(ref _nextConnId)}");
	}

	public static IEventStoreConnection CreateMiniNodeClient(IPEndPoint endPoint, TcpType tcpType = TcpType.Ssl,
		UserCredentials userCredentials = null)
	{
		return EventStoreConnection.Create(Settings(
				tcpType,
				userCredentials,
				limitAttemptsForOperationTo: 10,
				reconnectionDelay: TimeSpan.FromMilliseconds(100)),
			endPoint.ToESTcpUri(),
			$"ESC-{Interlocked.Increment(ref _nextConnId)}");
	}

	public static IEventStoreConnection To(MiniNode miniNode, TcpType tcpType,
		UserCredentials userCredentials = null)
	{
		return EventStoreConnection.Create(Settings(tcpType, userCredentials),
			miniNode.TcpEndPoint.ToESTcpUri(),
			$"ESC-{Interlocked.Increment(ref _nextConnId)}");
	}

	private static ConnectionSettingsBuilder Settings(
		TcpType tcpType,
		UserCredentials userCredentials,
		int limitAttemptsForOperationTo = 1,
		TimeSpan? reconnectionDelay = null)
	{
		var settings = ConnectionSettings.Create()
			.SetDefaultUserCredentials(userCredentials)
			.UseCustomLogger(ClientApiLoggerBridge.Default)
			.EnableVerboseLogging()
			.LimitReconnectionsTo(10)
			.LimitAttemptsForOperationTo(limitAttemptsForOperationTo)
			.SetTimeoutCheckPeriodTo(TimeSpan.FromMilliseconds(100))
			.SetReconnectionDelayTo(reconnectionDelay ?? TimeSpan.Zero)
			.FailOnNoServerResponse()
			//.SetOperationTimeoutTo(TimeSpan.FromDays(1))
			;
		if (tcpType == TcpType.Ssl)
		{
			settings.DisableServerCertificateValidation();
		}
		else
		{
			settings.DisableTls();
		}

		return settings;
	}
}
