using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Tests.Helpers;

public static class PortsHelper
{
	private static readonly ILogger Log =
		Serilog.Log.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "PortsHelper");
	private static readonly object Sync = new();
	private static readonly HashSet<int> ReservedPorts = [];

	public static int GetAvailablePort(IPAddress ip)
	{
		lock (Sync)
		{
			while (true)
			{
				TcpListener listener = new TcpListener(ip, 0);
				listener.Start();
				int port = ((IPEndPoint)listener.LocalEndpoint).Port;
				listener.Stop();

				if (!ReservedPorts.Add(port))
					continue;

				Log.Information($"Available port found: {port}");
				return port;
			}
		}
	}

	public static IPEndPoint GetLoopback()
	{
		var ip = IPAddress.Loopback;
		int port = PortsHelper.GetAvailablePort(ip);
		return new IPEndPoint(ip, port);
	}
}
