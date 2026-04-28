using System;

namespace EventStore.ClusterNode.Components.Services;

public static class UiMessages {
	public static string Friendly(Exception ex) =>
		string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}
