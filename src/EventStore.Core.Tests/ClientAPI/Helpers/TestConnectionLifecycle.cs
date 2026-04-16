using System;
using System.Threading.Tasks;
using EventStore.ClientAPI;

namespace EventStore.Core.Tests.ClientAPI.Helpers;

public static class TestConnectionLifecycle
{
	public static async Task<IEventStoreConnection> ReconnectUntilReady(
		Func<IEventStoreConnection> createConnection,
		Func<IEventStoreConnection, Task> readinessProbe,
		TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;

		while (true)
		{
			IEventStoreConnection connection = null;

			try
			{
				connection = createConnection();
				await connection.ConnectAsync();
				await readinessProbe(connection);
				return connection;
			}
			catch (Exception ex) when (IsTransientConnectionFailure(ex) && DateTime.UtcNow < deadline)
			{
				if (connection != null)
					TryCloseConnection(connection);
				await Task.Delay(250);
			}
		}
	}

	public static async Task CloseConnectionAndWait(IEventStoreConnection connection, TimeSpan timeout)
	{
		var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Closed += (_, _) => closed.TrySetResult();
		connection.Close();
		await closed.Task.WithTimeout(timeout);
	}

	public static bool IsTransientConnectionFailure(Exception ex) =>
		ex.GetType().Name is "ConnectionClosedException"
			or "RetriesLimitReachedException"
			or "NotAuthenticatedException"
			or "AccessDeniedException";

	public static void TryCloseConnection(IEventStoreConnection connection)
	{
		try
		{
			connection.Close();
		}
		catch
		{
		}
	}

	public static void DisposeIfNeeded(object candidate)
	{
		if (candidate is IDisposable disposable)
			disposable.Dispose();
	}
}
