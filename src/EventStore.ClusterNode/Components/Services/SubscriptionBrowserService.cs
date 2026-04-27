using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class SubscriptionBrowserService(
	IPublisher publisher,
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation StatisticsOperation = new(Operations.Subscriptions.Statistics);

	public async Task<SubscriptionListPage> ReadAll(CancellationToken cancellationToken = default) {
		if (!await HasAccess(StatisticsOperation, cancellationToken))
			return SubscriptionListPage.Unavailable("Persistent subscription statistics access was denied.");

		var envelope = new TaskCompletionEnvelope<MonitoringMessage.GetPersistentSubscriptionStatsCompleted>();
		publisher.Publish(new MonitoringMessage.GetAllPersistentSubscriptionStats(envelope));

		MonitoringMessage.GetPersistentSubscriptionStatsCompleted completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionListPage.Unavailable("Timed out reading persistent subscriptions.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionListPage.Unavailable($"Unable to read persistent subscriptions: {FriendlyMessage(ex)}");
		}

		return completed.Result switch {
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success =>
				SubscriptionListPage.Success(completed.SubscriptionStats
					?.Select(SubscriptionView.From)
					.OrderBy(x => x.StreamId, StringComparer.OrdinalIgnoreCase)
					.ThenBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
					.ToArray() ?? Array.Empty<SubscriptionView>()),
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotFound =>
				SubscriptionListPage.Success(Array.Empty<SubscriptionView>()),
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady =>
				SubscriptionListPage.Unavailable("Persistent subscriptions are not ready."),
			_ => SubscriptionListPage.Unavailable(string.IsNullOrWhiteSpace(completed.ErrorString)
				? $"Unable to read persistent subscriptions. Result: {completed.Result}."
				: completed.ErrorString)
		};
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static string FriendlyMessage(Exception ex) =>
		string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

	private sealed class TaskCompletionEnvelope<T> : IEnvelope where T : Message {
		private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<T> Task => _source.Task;

		public void ReplyWith<U>(U message) where U : Message {
			if (message is T typed) {
				_source.TrySetResult(typed);
				return;
			}

			_source.TrySetException(new InvalidOperationException(
				$"Expected {typeof(T).Name} but received {message.GetType().Name}."));
		}
	}
}

public sealed record SubscriptionListPage(
	IReadOnlyList<SubscriptionView> Subscriptions,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasSubscriptions => Subscriptions.Count > 0;
	public int ConnectedCount => Subscriptions.Count(x => x.ConnectionCount > 0);
	public long ParkedMessageCount => Subscriptions.Sum(x => x.ParkedMessageCount);
	public int InFlightMessageCount => Subscriptions.Sum(x => x.TotalInFlightMessages);
	public string GroupCountLabel => IsAvailable ? Subscriptions.Count.ToString() : "-";
	public string ConnectedCountLabel => IsAvailable ? ConnectedCount.ToString() : "-";
	public string ParkedMessageCountLabel => IsAvailable ? ParkedMessageCount.ToString() : "-";
	public string InFlightMessageCountLabel => IsAvailable ? InFlightMessageCount.ToString() : "-";

	public static SubscriptionListPage Success(IReadOnlyList<SubscriptionView> subscriptions) => new(subscriptions, "");
	public static SubscriptionListPage Unavailable(string message) => new(Array.Empty<SubscriptionView>(), message);
}

public sealed record SubscriptionView(
	string StreamId,
	string GroupName,
	string Status,
	string LastCheckpointedEventPosition,
	string LastKnownEventPosition,
	string StartFrom,
	string NamedConsumerStrategy,
	int AveragePerSecond,
	long TotalItems,
	int ConnectionCount,
	long ParkedMessageCount,
	int TotalInFlightMessages,
	int OutstandingMessagesCount,
	int ReadBufferCount,
	long LiveBufferCount,
	int RetryBufferCount,
	int MaxSubscriberCount,
	IReadOnlyList<SubscriptionConnectionView> Connections) {
	public string StatusLabel => string.IsNullOrWhiteSpace(Status) ? "Unknown" : Status;
	public string StatusTone => StatusLabel.Contains("live", StringComparison.OrdinalIgnoreCase)
		? "good"
		: StatusLabel.Contains("parked", StringComparison.OrdinalIgnoreCase) ||
		  StatusLabel.Contains("behind", StringComparison.OrdinalIgnoreCase)
			? "warn"
			: ConnectionCount == 0
				? "muted"
				: "";
	public string ConnectionLabel => ConnectionCount == 1 ? "1 consumer" : $"{ConnectionCount} consumers";
	public string DetailHref =>
		$"/subscriptions/{Uri.EscapeDataString(StreamId)}/{Uri.EscapeDataString(GroupName)}/info";
	public string ParkedHref =>
		$"/subscriptions/{Uri.EscapeDataString(StreamId)}/{Uri.EscapeDataString(GroupName)}/parked";

	public static SubscriptionView From(MonitoringMessage.PersistentSubscriptionInfo info) {
		var connections = info.Connections?
			.Select(SubscriptionConnectionView.FromInfo)
			.OrderBy(x => x.ConnectionName, StringComparer.OrdinalIgnoreCase)
			.ToArray() ?? Array.Empty<SubscriptionConnectionView>();

		return new(
			info.EventSource ?? "",
			info.GroupName ?? "",
			info.Status ?? "",
			info.LastCheckpointedEventPosition ?? "",
			info.LastKnownEventPosition ?? "",
			info.StartFrom ?? "",
			info.NamedConsumerStrategy ?? "",
			info.AveragePerSecond,
			info.TotalItems,
			connections.Length,
			info.ParkedMessageCount,
			info.TotalInFlightMessages,
			info.OutstandingMessagesCount,
			info.ReadBufferCount,
			info.LiveBufferCount,
			info.RetryBufferCount,
			info.MaxSubscriberCount,
			connections);
	}
}

public sealed record SubscriptionConnectionView(
	string ConnectionName,
	string From,
	string Username,
	int AverageItemsPerSecond,
	long TotalItems,
	int AvailableSlots,
	int InFlightMessages) {
	public string DisplayName => string.IsNullOrWhiteSpace(ConnectionName) ? From : ConnectionName;

	public static SubscriptionConnectionView FromInfo(MonitoringMessage.ConnectionInfo info) =>
		new(
			info.ConnectionName ?? "",
			info.From ?? "",
			info.Username ?? "",
			info.AverageItemsPerSecond,
			info.TotalItems,
			info.AvailableSlots,
			info.InFlightMessages);
}
