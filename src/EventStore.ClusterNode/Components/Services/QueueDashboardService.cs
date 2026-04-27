using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace EventStore.ClusterNode.Components.Services;

public sealed class QueueDashboardService(
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor,
	INodeHttpClientFactory nodeHttpClientFactory) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation StatisticsOperation = new(Operations.Node.Statistics.Read);

	public async Task<QueueDashboardPage> Read(CancellationToken cancellationToken = default) {
		if (!await HasAccess(StatisticsOperation, cancellationToken))
			return QueueDashboardPage.Unavailable("Runtime statistics access was denied.");

		try {
			var context = httpContextAccessor.HttpContext;
			if (context is null)
				return QueueDashboardPage.Unavailable("Runtime statistics are unavailable outside an HTTP request.");

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(ReadTimeout);
			using var client = nodeHttpClientFactory.CreateHttpClient(new[] { context.Request.Host.Host });
			using var request = new HttpRequestMessage(HttpMethod.Get, BuildStatsUri(context.Request));
			CopyHeader(context.Request, request, HeaderNames.Authorization);
			CopyHeader(context.Request, request, HeaderNames.Cookie);

			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
				return QueueDashboardPage.Unavailable("Runtime statistics access was denied.");

			if (!response.IsSuccessStatusCode)
				return QueueDashboardPage.Unavailable(
					$"Queue statistics endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");

			await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
			using var document = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
			return ParseQueueStats(document);
		} catch (TimeoutException) {
			return QueueDashboardPage.Unavailable("Timed out reading queue statistics.");
		} catch (OperationCanceledException) {
			if (cancellationToken.IsCancellationRequested)
				throw;

			return QueueDashboardPage.Unavailable("Timed out reading queue statistics.");
		} catch (Exception ex) {
			return QueueDashboardPage.Unavailable($"Unable to read queue statistics: {FriendlyMessage(ex)}");
		}
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static Uri BuildStatsUri(HttpRequest request) {
		var builder = new UriBuilder(request.Scheme, request.Host.Host) {
			Path = $"{request.PathBase}/stats",
			Query = "format=json"
		};

		if (request.Host.Port.HasValue)
			builder.Port = request.Host.Port.Value;
		else
			builder.Port = -1;

		return builder.Uri;
	}

	private static void CopyHeader(HttpRequest source, HttpRequestMessage target, string headerName) {
		if (source.Headers.TryGetValue(headerName, out var value) && value.Count > 0)
			target.Headers.TryAddWithoutValidation(headerName, value.ToArray());
	}

	private static QueueDashboardPage ParseQueueStats(JsonDocument document) {
		if (!document.RootElement.TryGetProperty("es", out var es) ||
		    !es.TryGetProperty("queue", out var queueStats) ||
		    queueStats.ValueKind != JsonValueKind.Object) {
			return QueueDashboardPage.Unavailable("Queue statistics are unavailable.");
		}

		var queues = new List<QueueDashboardRow>();
		foreach (var entry in queueStats.EnumerateObject()) {
			if (QueueDashboardRow.TryFrom(entry, out var queue))
				queues.Add(queue);
		}

		return QueueDashboardPage.Success(queues);
	}

	private static string FriendlyMessage(Exception ex) =>
		string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

public sealed record QueueDashboardPage(
	IReadOnlyList<QueueDashboardBlock> Blocks,
	IReadOnlyList<QueueDashboardRow> Queues,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasRows => Blocks.Count > 0;
	public int BusyQueueCount => Queues.Count(x => x.IsBusy);
	public int TotalBacklog => Queues.Sum(x => x.Length);
	public int TotalRate => Queues.Sum(x => x.AvgItemsPerSecond);
	public string QueueCountLabel => IsAvailable ? Queues.Count.ToString(CultureInfo.InvariantCulture) : "-";
	public string BusyQueueCountLabel => IsAvailable ? BusyQueueCount.ToString(CultureInfo.InvariantCulture) : "-";
	public string TotalBacklogLabel => IsAvailable ? TotalBacklog.ToString(CultureInfo.InvariantCulture) : "-";
	public string TotalRateLabel => IsAvailable ? TotalRate.ToString(CultureInfo.InvariantCulture) : "-";

	public static QueueDashboardPage Success(IReadOnlyList<QueueDashboardRow> queues) =>
		new(BuildBlocks(queues), queues, "");

	public static QueueDashboardPage Unavailable(string message) =>
		new(Array.Empty<QueueDashboardBlock>(), Array.Empty<QueueDashboardRow>(), message);

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

	public static bool TryFrom(JsonProperty entry, out QueueDashboardRow row) {
		if (entry.Value.ValueKind != JsonValueKind.Object) {
			row = null;
			return false;
		}

		row = new QueueDashboardRow(
			QueueDashboardRowKind.Queue,
			ReadString(entry.Value, "queueName", entry.Name),
			ReadString(entry.Value, "groupName", ""),
			ReadInt(entry.Value, "length"),
			ReadLong(entry.Value, "lengthCurrentTryPeak"),
			ReadLong(entry.Value, "lengthLifetimePeak"),
			ReadInt(entry.Value, "avgItemsPerSecond"),
			ReadDouble(entry.Value, "avgProcessingTime"),
			ReadLong(entry.Value, "totalItemsProcessed"),
			ReadString(entry.Value, "inProgressMessage", "<none>"),
			ReadString(entry.Value, "lastProcessedMessage", "<none>"));
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

	private static string ReadString(JsonElement stats, string key, string fallback) {
		if (!stats.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return fallback;

		if (value.ValueKind == JsonValueKind.String)
			return value.GetString() ?? fallback;

		return value.ToString();
	}

	private static int ReadInt(JsonElement stats, string key) =>
		(int)Math.Min(int.MaxValue, Math.Max(int.MinValue, ReadLong(stats, key)));

	private static long ReadLong(JsonElement stats, string key) {
		if (!stats.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return 0;

		if (value.ValueKind == JsonValueKind.Number) {
			if (value.TryGetInt64(out var integer))
				return integer;

			if (value.TryGetDouble(out var numeric))
				return Convert.ToInt64(numeric);
		}

		return value.ValueKind == JsonValueKind.String &&
		       long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: 0;
	}

	private static double ReadDouble(JsonElement stats, string key) {
		if (!stats.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return 0;

		if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
			return numeric;

		return value.ValueKind == JsonValueKind.String &&
		       double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: 0;
	}

	private static string DisplayMessage(string message) =>
		string.IsNullOrWhiteSpace(message) ? "<none>" : message;

	private static bool IsPlaceholderMessage(string message) =>
		string.IsNullOrWhiteSpace(message) ||
		string.Equals(message, "<none>", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(message, "n/a", StringComparison.OrdinalIgnoreCase);
}
