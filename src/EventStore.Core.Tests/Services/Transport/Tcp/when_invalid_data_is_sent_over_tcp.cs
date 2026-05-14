using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Tests.Integration;
using EventStore.Transport.Tcp;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Tcp;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_invalid_data_is_sent_over_tcp<TLogFormat, TStreamId> : specification_with_cluster<TLogFormat, TStreamId>
{

	[Timeout(15000)]
	[TestCase("InternalTcpEndPoint", false)]
	[TestCase("ExternalTcpEndPoint", false)]
	public async Task connection_should_be_closed_by_remote_party(string endpointProperty, bool secure)
	{
		IPEndPoint endpoint = (IPEndPoint)_nodes[0].GetType().GetProperty(endpointProperty).GetValue(_nodes[0], null);
		await WaitForEndpoint(endpoint);

		var closedEvent = new ManualResetEventSlim();
		TaskCompletionSource<SocketError> connectionResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

		ITcpConnection connection;
		if (!secure)
		{
			connection = TcpConnection.CreateConnectingTcpConnection(
				Guid.NewGuid(),
				endpoint,
				new TcpClientConnector(),
				TimeSpan.FromSeconds(5),
				(conn) => connectionResult.TrySetResult(SocketError.Success),
				(conn, error) => connectionResult.TrySetResult(error),
				false);
		}
		else
		{
			connection = TcpConnectionSsl.CreateConnectingConnection(
				Guid.NewGuid(),
				endpoint.GetHost(),
				null,
				endpoint,
				delegate
				{ return (true, null); },
				null,
				new TcpClientConnector(),
				TimeSpan.FromSeconds(5),
				(conn) => connectionResult.TrySetResult(SocketError.Success),
				(conn, error) => connectionResult.TrySetResult(error),
				false);
		}

		connection.ConnectionClosed += (conn, error) => closedEvent.Set();

		SocketError result = await connectionResult.Task.WithTimeout();
		Assert.AreEqual(SocketError.Success, result);
		var data = new List<ArraySegment<byte>> {
			new ArraySegment<byte>(new byte[] {1, 2, 3, 4, 5, 6, 7, 8, 9, 10})
		};
		connection.EnqueueSend(data);
		Assert.True(closedEvent.Wait(10000));
		connection.Close("intentional close");
	}

	private static async Task WaitForEndpoint(IPEndPoint endpoint)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

		while (!timeout.IsCancellationRequested)
		{
			using var client = new TcpClient();

			try
			{
				await client.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
				return;
			}
			catch (Exception ex) when (ex is SocketException or OperationCanceledException)
			{
				await Task.Delay(100, CancellationToken.None);
			}
		}

		throw new TimeoutException($"TCP endpoint {endpoint} did not accept connections before the test timeout.");
	}
}
