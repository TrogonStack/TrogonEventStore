using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class QueueDashboardService {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation StatisticsOperation = new(Operations.Node.Statistics.Read);
	private static readonly Operation TcpStatisticsOperation = new(Operations.Node.Statistics.Tcp);

	private readonly IAuthorizationProvider _authorizationProvider;
	private readonly IHttpContextAccessor _httpContextAccessor;
	private readonly IPublisher _monitoringQueue;
	private readonly object _tcpGate = new();
	private Dictionary<Guid, TcpConnectionRow> _previousTcpConnections = new();
	private DateTime? _lastTcpRefresh;

	public QueueDashboardService(
		IAuthorizationProvider authorizationProvider,
		IHttpContextAccessor httpContextAccessor,
		StandardComponents standardComponents) {
		_authorizationProvider = authorizationProvider;
		_httpContextAccessor = httpContextAccessor;
		_monitoringQueue = standardComponents.MonitoringQueue;
	}

	public async Task<QueueDashboardPage> Read(CancellationToken cancellationToken = default) {
		if (!await HasAccess(StatisticsOperation, cancellationToken))
			return QueueDashboardPage.Unavailable("Runtime statistics access was denied.");

		try {
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(ReadTimeout);

			var queues = await ReadQueueStats(timeout.Token);
			var tcp = await ReadTcpStatsSafe(timeout.Token, cancellationToken);
			return QueueDashboardPage.Success(queues, tcp.Rows, tcp.Message);
		} catch (TimeoutException) {
			return QueueDashboardPage.Unavailable("Timed out reading queue statistics.");
		} catch (OperationCanceledException) {
			if (cancellationToken.IsCancellationRequested)
				throw;

			return QueueDashboardPage.Unavailable("Timed out reading queue statistics.");
		} catch (Exception ex) {
			return QueueDashboardPage.Unavailable($"Unable to read queue statistics: {UiMessages.Friendly(ex)}");
		}
	}

	private ClaimsPrincipal CurrentUser =>
		_httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		_authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private async Task<IReadOnlyList<QueueDashboardRow>> ReadQueueStats(CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<MonitoringMessage.GetFreshStatsCompleted>();
		_monitoringQueue.Publish(new MonitoringMessage.GetFreshStats(envelope, x => x, useMetadata: false, useGrouping: true));
		var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		if (!completed.Success ||
		    !QueueDashboardStats.TryReadDictionary(completed.Stats, "es", out var es) ||
		    !QueueDashboardStats.TryReadDictionary(es, "queue", out var queueStats))
			return Array.Empty<QueueDashboardRow>();

		var queues = new List<QueueDashboardRow>();
		foreach (var entry in queueStats) {
			if (QueueDashboardRow.TryFrom(entry, out var queue))
				queues.Add(queue);
		}

		return queues;
	}

	private async Task<TcpConnectionResult> ReadTcpStats(CancellationToken cancellationToken) {
		if (!await HasAccess(TcpStatisticsOperation, cancellationToken))
			return new TcpConnectionResult(Array.Empty<TcpConnectionRow>(), "TCP statistics access was denied.");

		var envelope = new TaskCompletionEnvelope<MonitoringMessage.GetFreshTcpConnectionStatsCompleted>();
		_monitoringQueue.Publish(new MonitoringMessage.GetFreshTcpConnectionStats(envelope));
		var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		return BuildTcpRows(completed.ConnectionStats ?? []);
	}

	private async Task<TcpConnectionResult> ReadTcpStatsSafe(
		CancellationToken timeoutToken,
		CancellationToken cancellationToken) {
		try {
			return await ReadTcpStats(timeoutToken);
		} catch (TimeoutException) {
			return new TcpConnectionResult(Array.Empty<TcpConnectionRow>(), "Timed out reading TCP statistics.");
		} catch (OperationCanceledException) {
			if (cancellationToken.IsCancellationRequested)
				throw;

			return new TcpConnectionResult(Array.Empty<TcpConnectionRow>(), "Timed out reading TCP statistics.");
		} catch (Exception ex) {
			return new TcpConnectionResult(
				Array.Empty<TcpConnectionRow>(),
				$"Unable to read TCP statistics: {UiMessages.Friendly(ex)}");
		}
	}

	private TcpConnectionResult BuildTcpRows(IReadOnlyList<MonitoringMessage.TcpConnectionStats> connections) {
		lock (_tcpGate) {
			var now = DateTime.UtcNow;
			var elapsedSeconds = _lastTcpRefresh.HasValue
				? Math.Max(1, (now - _lastTcpRefresh.Value).TotalSeconds)
				: 1;

			var rows = connections
				.Select(x => TcpConnectionRow.From(x, _previousTcpConnections.GetValueOrDefault(x.ConnectionId), elapsedSeconds))
				.OrderBy(x => x.ClientConnectionName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(x => x.ConnectionId)
				.ToArray();

			_previousTcpConnections = rows.ToDictionary(x => x.ConnectionId);
			_lastTcpRefresh = now;
			return new TcpConnectionResult(rows, "");
		}
	}

}

file static class QueueDashboardStats {
	public static bool TryReadDictionary(
		IReadOnlyDictionary<string, object> stats,
		string key,
		out IReadOnlyDictionary<string, object> value) {
		value = null;
		return stats is not null &&
		       stats.TryGetValue(key, out var item) &&
		       TryReadDictionary(item, out value);
	}

	public static bool TryReadDictionary(object value, out IReadOnlyDictionary<string, object> dictionary) {
		dictionary = value switch {
			IDictionary<string, object> mutable => new Dictionary<string, object>(mutable, StringComparer.OrdinalIgnoreCase),
			IReadOnlyDictionary<string, object> readOnly => new Dictionary<string, object>(readOnly, StringComparer.OrdinalIgnoreCase),
			_ => null
		};

		return dictionary is not null;
	}
}

public sealed record QueueDashboardPage(
	IReadOnlyList<QueueDashboardBlock> Blocks,
	IReadOnlyList<QueueDashboardRow> Queues,
	IReadOnlyList<TcpConnectionRow> TcpConnections,
	string TcpMessage,
	string Message) {
	private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasRows => Blocks.Count > 0;
	public int BusyQueueCount => Queues.Count(x => x.IsBusy);
	public int TotalBacklog => Queues.Sum(x => x.Length);
	public int TotalRate => Queues.Sum(x => x.AvgItemsPerSecond);
	public string QueueCountLabel => IsAvailable ? Queues.Count.ToString(CultureInfo.InvariantCulture) : "-";
	public string BusyQueueCountLabel => IsAvailable ? BusyQueueCount.ToString(CultureInfo.InvariantCulture) : "-";
	public string TotalBacklogLabel => IsAvailable ? TotalBacklog.ToString(CultureInfo.InvariantCulture) : "-";
	public string TotalRateLabel => IsAvailable ? TotalRate.ToString(CultureInfo.InvariantCulture) : "-";
	public string ClientPayloadJson => JsonSerializer.Serialize(
		new QueueDashboardPayload(
			Queues.Select(QueuePayload.From).ToArray(),
			TcpConnections.Select(TcpConnectionPayload.From).ToArray(),
			Message,
			TcpMessage),
		PayloadJsonOptions);

	public static QueueDashboardPage Success(
		IReadOnlyList<QueueDashboardRow> queues,
		IReadOnlyList<TcpConnectionRow> tcpConnections,
		string tcpMessage) =>
		new(BuildBlocks(queues), queues, tcpConnections, tcpMessage, "");

	public static QueueDashboardPage Unavailable(string message) =>
		new(
			Array.Empty<QueueDashboardBlock>(),
			Array.Empty<QueueDashboardRow>(),
			Array.Empty<TcpConnectionRow>(),
			message,
			message);

	private static IReadOnlyList<QueueDashboardBlock> BuildBlocks(IReadOnlyList<QueueDashboardRow> queues) {
		var blocks = new List<QueueDashboardBlock>();

		foreach (var queue in queues.Where(x => string.IsNullOrWhiteSpace(x.GroupName)))
			blocks.Add(new QueueDashboardBlock(queue.Name, queue, Array.Empty<QueueDashboardRow>()));

		foreach (var group in queues
			         .Where(x => !string.IsNullOrWhiteSpace(x.GroupName))
			         .GroupBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)) {
			var children = group
				.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.Select(x => x.AsGroupMember())
				.ToArray();

			blocks.Add(new QueueDashboardBlock(group.Key, QueueDashboardRow.Group(group.Key, children), children));
		}

		return blocks.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
	}
}

public sealed record QueueDashboardBlock(
	string Name,
	QueueDashboardRow Summary,
	IReadOnlyList<QueueDashboardRow> Children) {
	public bool HasChildren => Children.Count > 0;
}

public sealed record TcpConnectionResult(
	IReadOnlyList<TcpConnectionRow> Rows,
	string Message);

public sealed record QueueDashboardPayload(
	IReadOnlyList<QueuePayload> Queues,
	IReadOnlyList<TcpConnectionPayload> TcpConnections,
	string Message,
	string TcpMessage);

public sealed record QueuePayload(
	string Kind,
	string Name,
	string GroupName,
	int Length,
	long LengthCurrentTryPeak,
	long LengthLifetimePeak,
	int AvgItemsPerSecond,
	double AvgProcessingTime,
	long TotalItemsProcessed,
	string InProgressMessage,
	string LastProcessedMessage) {
	public static QueuePayload From(QueueDashboardRow row) =>
		new(
			"queue",
			row.Name,
			row.GroupName,
			row.Length,
			row.LengthCurrentTryPeak,
			row.LengthLifetimePeak,
			row.AvgItemsPerSecond,
			row.AvgProcessingTime,
			row.TotalItemsProcessed,
			row.InProgressMessage,
			row.LastProcessedMessage);
}

public sealed record TcpConnectionPayload(
	Guid ConnectionId,
	string ClientConnectionName,
	string RemoteEndPoint,
	string LocalEndPoint,
	long TotalBytesSent,
	long TotalBytesReceived,
	int PendingSendBytes,
	int PendingReceivedBytes,
	double SentRate,
	double ReceivedRate,
	bool IsExternalConnection,
	bool IsSslConnection) {
	public static TcpConnectionPayload From(TcpConnectionRow row) =>
		new(
			row.ConnectionId,
			row.ClientConnectionName,
			row.RemoteEndPoint,
			row.LocalEndPoint,
			row.TotalBytesSent,
			row.TotalBytesReceived,
			row.PendingSendBytes,
			row.PendingReceivedBytes,
			row.SentRate,
			row.ReceivedRate,
			row.IsExternalConnection,
			row.IsSslConnection);
}

public sealed record TcpConnectionRow(
	Guid ConnectionId,
	string ClientConnectionName,
	string RemoteEndPoint,
	string LocalEndPoint,
	long TotalBytesSent,
	long TotalBytesReceived,
	int PendingSendBytes,
	int PendingReceivedBytes,
	double SentRate,
	double ReceivedRate,
	bool IsExternalConnection,
	bool IsSslConnection) {
	public string IdLabel => ConnectionId == Guid.Empty ? "<none>" : ConnectionId.ToString("D");
	public string ClientLabel => DisplayMessage(ClientConnectionName);
	public string TypeLabel => $"{(IsExternalConnection ? "External" : "Internal")} {(IsSslConnection ? "TLS" : "TCP")}";
	public string RemoteEndPointLabel => DisplayMessage(RemoteEndPoint);
	public string SentRateLabel => FormatByteRate(SentRate);
	public string ReceivedRateLabel => FormatByteRate(ReceivedRate);
	public string TotalBytesSentLabel => TotalBytesSent.ToString("N0", CultureInfo.InvariantCulture);
	public string TotalBytesReceivedLabel => TotalBytesReceived.ToString("N0", CultureInfo.InvariantCulture);
	public string PendingSendBytesLabel => PendingSendBytes.ToString("N0", CultureInfo.InvariantCulture);
	public string PendingReceivedBytesLabel => PendingReceivedBytes.ToString("N0", CultureInfo.InvariantCulture);

	public static TcpConnectionRow From(
		MonitoringMessage.TcpConnectionStats stats,
		TcpConnectionRow previous,
		double elapsedSeconds) {
		var sentRate = previous is null
			? 0
			: Math.Max(0, (stats.TotalBytesSent - previous.TotalBytesSent) / elapsedSeconds);
		var receivedRate = previous is null
			? 0
			: Math.Max(0, (stats.TotalBytesReceived - previous.TotalBytesReceived) / elapsedSeconds);

		return new TcpConnectionRow(
			stats.ConnectionId,
			stats.ClientConnectionName ?? "",
			stats.RemoteEndPoint ?? "",
			stats.LocalEndPoint ?? "",
			stats.TotalBytesSent,
			stats.TotalBytesReceived,
			stats.PendingSendBytes,
			stats.PendingReceivedBytes,
			sentRate,
			receivedRate,
			stats.IsExternalConnection,
			stats.IsSslConnection);
	}

	private static string DisplayMessage(string value) =>
		string.IsNullOrWhiteSpace(value) ? "<none>" : value;

	private static string FormatByteRate(double value) =>
		$"{Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)} B/s";
}

public enum QueueDashboardRowKind {
	Queue,
	GroupSummary,
	GroupMember
}

public sealed record QueueDashboardRow(
	QueueDashboardRowKind Kind,
	string Name,
	string GroupName,
	int Length,
	long LengthCurrentTryPeak,
	long LengthLifetimePeak,
	int AvgItemsPerSecond,
	double AvgProcessingTime,
	long TotalItemsProcessed,
	string InProgressMessage,
	string LastProcessedMessage) {
	public bool IsBusy => !IsPlaceholderMessage(InProgressMessage);
	public bool IsGroupSummary => Kind == QueueDashboardRowKind.GroupSummary;
	public bool IsGroupMember => Kind == QueueDashboardRowKind.GroupMember;
	public string LengthLabel => LengthCurrentTryPeak.ToString(CultureInfo.InvariantCulture);
	public string LengthPeakLabel => LengthLifetimePeak.ToString(CultureInfo.InvariantCulture);
	public string AvgItemsPerSecondLabel => AvgItemsPerSecond.ToString(CultureInfo.InvariantCulture);
	public string AvgProcessingTimeLabel => AvgProcessingTime.ToString("0.000", CultureInfo.InvariantCulture);
	public string TotalItemsProcessedLabel => TotalItemsProcessed.ToString(CultureInfo.InvariantCulture);
	public string CurrentLastMessageLabel => IsGroupSummary
		? "n/a"
		: $"{DisplayMessage(InProgressMessage)} / {DisplayMessage(LastProcessedMessage)}";
	public string NameClass => IsGroupMember
		? "pl-8 font-bold text-es-muted"
		: "font-black text-es-ink";
	public string RowClass => IsGroupSummary
		? "bg-es-ink/5 font-black text-es-ink"
		: IsGroupMember
			? "bg-white/45 text-es-muted"
			: "bg-white/70 text-es-ink";
	public string KindLabel => Kind switch {
		QueueDashboardRowKind.GroupSummary => "Group",
		QueueDashboardRowKind.GroupMember => "Member",
		_ => "Queue"
	};

	public QueueDashboardRow AsGroupMember() => this with { Kind = QueueDashboardRowKind.GroupMember };

	public static bool TryFrom(KeyValuePair<string, object> entry, out QueueDashboardRow row) {
		if (!QueueDashboardStats.TryReadDictionary(entry.Value, out var stats)) {
			row = null;
			return false;
		}

		row = new QueueDashboardRow(
			QueueDashboardRowKind.Queue,
			ReadString(stats, "queueName", entry.Key),
			ReadString(stats, "groupName", ""),
			ReadInt(stats, "length"),
			ReadLong(stats, "lengthCurrentTryPeak"),
			ReadLong(stats, "lengthLifetimePeak"),
			ReadInt(stats, "avgItemsPerSecond"),
			ReadDouble(stats, "avgProcessingTime"),
			ReadLong(stats, "totalItemsProcessed"),
			ReadString(stats, "inProgressMessage", "<none>"),
			ReadString(stats, "lastProcessedMessage", "<none>"));
		return true;
	}

	public static QueueDashboardRow Group(string groupName, IReadOnlyList<QueueDashboardRow> children) =>
		new(
			QueueDashboardRowKind.GroupSummary,
			groupName,
			groupName,
			children.Sum(x => x.Length),
			children.Count == 0 ? 0 : children.Max(x => x.LengthCurrentTryPeak),
			children.Count == 0 ? 0 : children.Max(x => x.LengthLifetimePeak),
			children.Sum(x => x.AvgItemsPerSecond),
			WeightedAverageProcessingTime(children),
			children.Sum(x => x.TotalItemsProcessed),
			"n/a",
			"n/a");

	private static double WeightedAverageProcessingTime(IReadOnlyList<QueueDashboardRow> rows) {
		var rate = rows.Sum(x => x.AvgItemsPerSecond);
		if (rate > 0)
			return rows.Sum(x => x.AvgProcessingTime * x.AvgItemsPerSecond) / rate;

		var processed = rows.Sum(x => x.TotalItemsProcessed);
		if (processed > 0)
			return rows.Sum(x => x.AvgProcessingTime * x.TotalItemsProcessed) / processed;

		return rows.Count == 0 ? 0 : rows.Average(x => x.AvgProcessingTime);
	}

	private static string ReadString(IReadOnlyDictionary<string, object> stats, string key, string fallback) =>
		stats.TryGetValue(key, out var value) && value is not null
			? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback
			: fallback;

	private static int ReadInt(IReadOnlyDictionary<string, object> stats, string key) =>
		(int)Math.Min(int.MaxValue, Math.Max(int.MinValue, ReadLong(stats, key)));

	private static long ReadLong(IReadOnlyDictionary<string, object> stats, string key) {
		if (!stats.TryGetValue(key, out var value) || value is null)
			return 0;

		return value switch {
			long longValue => longValue,
			int intValue => intValue,
			uint uintValue => uintValue,
			ulong ulongValue => ulongValue > long.MaxValue ? long.MaxValue : (long)ulongValue,
			double doubleValue => Convert.ToInt64(doubleValue),
			float floatValue => Convert.ToInt64(floatValue),
			decimal decimalValue => Convert.ToInt64(decimalValue),
			_ => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
				CultureInfo.InvariantCulture, out var parsed)
				? parsed
				: 0
		};
	}

	private static double ReadDouble(IReadOnlyDictionary<string, object> stats, string key) {
		if (!stats.TryGetValue(key, out var value) || value is null)
			return 0;

		return value switch {
			double doubleValue => doubleValue,
			float floatValue => floatValue,
			decimal decimalValue => Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture),
			long longValue => longValue,
			int intValue => intValue,
			_ => double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
				CultureInfo.InvariantCulture, out var parsed)
				? parsed
				: 0
		};
	}

	private static string DisplayMessage(string message) =>
		string.IsNullOrWhiteSpace(message) ? "<none>" : message;

	private static bool IsPlaceholderMessage(string message) =>
		string.IsNullOrWhiteSpace(message) ||
		string.Equals(message, "<none>", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(message, "n/a", StringComparison.OrdinalIgnoreCase);
}
