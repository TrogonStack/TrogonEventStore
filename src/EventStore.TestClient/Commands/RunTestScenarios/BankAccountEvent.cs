using System;
using System.Collections.Generic;
using EventStore.ClientAPI;
using EventStore.Common.Utils;
using EventStore.TestClient.Commands;

namespace EventStore.TestClient.Commands.RunTestScenarios;

internal class BankAccountEvent {
	public static EventData FromEvent(object accountObject) {
		if (accountObject == null) {
			throw new ArgumentNullException("accountObject");
		}

		var type = accountObject.GetType().Name;
		var encodedData = Helper.UTF8NoBom.GetBytes(TestClientJson.To(accountObject));
		var encodedMetadata =
			Helper.UTF8NoBom.GetBytes(TestClientJson.To(new Dictionary<string, object> { { "IsEmpty", true } }));

		return new EventData(Guid.NewGuid(), type, true, encodedData, encodedMetadata);
	}
}
