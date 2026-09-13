using System;
using EventStore.Client;

namespace EventStore.TestClient.GrpcCommands;

internal readonly record struct ReadAllWorkload(
	Direction Direction,
	Position Position,
	int ClientCount)
{
	public static bool TryParse(string[] args, out ReadAllWorkload workload)
	{
		workload = new ReadAllWorkload(Direction.Forwards, Position.Start, 1);
		if (args.Length is not (0 or 1 or 2 or 4))
		{
			return false;
		}

		if (args.Length == 0)
		{
			return true;
		}

		var direction = args[0].ToUpperInvariant() switch
		{
			"F" => Direction.Forwards,
			"B" => Direction.Backwards,
			_ => (Direction?)null
		};
		if (direction is null)
		{
			return false;
		}

		try
		{
			var clientCount = args.Length >= 2 ? MetricPrefixValue.ParseInt(args[1]) : 1;
			if (clientCount <= 0)
			{
				return false;
			}

			var position = direction == Direction.Forwards ? Position.Start : Position.End;
			if (args.Length == 4)
			{
				if (!ulong.TryParse(args[2], out var commitPosition) ||
					!ulong.TryParse(args[3], out var preparePosition))
				{
					return false;
				}

				position = new Position(commitPosition, preparePosition);
			}

			workload = new ReadAllWorkload(direction.Value, position, clientCount);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
