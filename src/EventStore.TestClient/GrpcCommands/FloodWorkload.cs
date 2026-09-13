using System;
using EventStore.TestClient.Commands;

namespace EventStore.TestClient.GrpcCommands;

internal readonly record struct FloodWorkload(
	int ClientCount,
	long RequestCount,
	int MaxInFlight)
{
	public int MaxInFlightPerClient => Math.Max(1, MaxInFlight / ClientCount);

	public long RequestsForClient(int clientIndex)
	{
		if (clientIndex < 0 || clientIndex >= ClientCount)
		{
			throw new ArgumentOutOfRangeException(nameof(clientIndex));
		}

		return RequestCount / ClientCount + (clientIndex == ClientCount - 1 ? RequestCount % ClientCount : 0);
	}

	public static FloodWorkload Parse(string[] args, long defaultRequestCount, int maxInFlight)
	{
		var clientCount = args.Length == 0 ? 1 : MetricPrefixValue.ParseInt(args[0]);
		var requestCount = args.Length == 0 ? defaultRequestCount : MetricPrefixValue.ParseLong(args[1]);
		return Create(clientCount, requestCount, maxInFlight);
	}

	public static FloodWorkload Create(int clientCount, long requestCount, int maxInFlight)
	{
		if (clientCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(clientCount), "Client count must be positive.");
		}

		if (requestCount < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(requestCount), "Request count cannot be negative.");
		}

		return new FloodWorkload(clientCount, requestCount, maxInFlight);
	}
}
