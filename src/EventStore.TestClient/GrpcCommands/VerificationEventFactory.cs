using System;
using System.Globalization;
using System.Text;
using EventStore.Client;
using Newtonsoft.Json;

namespace EventStore.TestClient.GrpcCommands;

internal static class VerificationEventFactory
{
	private static readonly UTF8Encoding Utf8NoBom = new(false);

	public static EventData Create(int eventNumber)
	{
		var payload = CreatePayload(eventNumber);
		return new EventData(
			Uuid.FromGuid(Guid.NewGuid()),
			payload.GetType().Name,
			Utf8NoBom.GetBytes(JsonConvert.SerializeObject(payload)),
			contentType: "application/json");
	}

	private static object CreatePayload(int eventNumber)
	{
		var internalCounter = eventNumber + 1;
		if (internalCounter % 10 == 0)
		{
			var checkpointCount = internalCounter / 10;
			var elementsCount = internalCounter / 2;
			return new AccountCheckPoint(
				ComputeSum(20, elementsCount, 20) - ComputeSum(100, checkpointCount, 100),
				ComputeSum(10, elementsCount, 20));
		}

		return internalCounter % 2 == 0
			? new AccountCredited(internalCounter * 10, (internalCounter % 17).ToString(CultureInfo.InvariantCulture))
			: new AccountDebited(internalCounter * 10, (internalCounter % 17).ToString(CultureInfo.InvariantCulture));
	}

	private static int ComputeSum(int first, int count, int step) =>
		count * (2 * first + step * (count - 1)) / 2;

	private sealed record AccountCredited(decimal CreditedAmount, string Kind);
	private sealed record AccountDebited(decimal DebitedAmount, string Kind);
	private sealed record AccountCheckPoint(decimal CreditedAmount, decimal DebitedAmount);
}
