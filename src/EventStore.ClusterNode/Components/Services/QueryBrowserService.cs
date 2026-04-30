using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class QueryBrowserService(
	IPublisher publisher,
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation CreateQueryOperation = new Operation(Operations.Projections.Create)
		.WithParameter(Operations.Projections.Parameters.Query);

	public async Task<QueryRunPage> RunTransient(string query, CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(query))
			return QueryRunPage.Unavailable(query ?? "", "Enter a query before running it.");

		if (!await HasAccess(CreateQueryOperation, cancellationToken))
			return QueryRunPage.Unavailable(query, "Transient query access was denied.");

		var projectionName = Guid.NewGuid().ToString("D");
		var envelope = new TaskCompletionEnvelope<ProjectionManagementMessage.Updated>(
			mapFailure: message => message is ProjectionManagementMessage.OperationFailed failed ? failed.Reason ?? "" : null);

		ProjectionManagementMessage.Updated completed;
		try {
			publisher.Publish(new ProjectionManagementMessage.Command.Post(
				envelope,
				ProjectionMode.Transient,
				projectionName,
				new ProjectionManagementMessage.RunAs(CurrentUser),
				"JS",
				query,
				enabled: true,
				checkpointsEnabled: false,
				emitEnabled: false,
				trackEmittedStreams: false,
				enableRunAs: true));

			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return QueryRunPage.Unavailable(query,
				$"Timed out creating transient query '{projectionName}'. It may still be visible temporarily before automatic expiry.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return QueryRunPage.Unavailable(query, $"Unable to create transient query: {UiMessages.Friendly(ex)}");
		}

		return QueryRunPage.Success(query, completed.Name);
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

}

public sealed record QueryRunPage(
	string Query,
	string ProjectionName,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasProjection => !string.IsNullOrWhiteSpace(ProjectionName);
	public string ProjectionHref => HasProjection ? $"/ui/projections/{Uri.EscapeDataString(ProjectionName)}" : "";
	public string DebugHref => HasProjection ? $"/ui/projections/debug/{Uri.EscapeDataString(ProjectionName)}" : "";

	public static QueryRunPage Success(string query, string projectionName) => new(query, projectionName, "");
	public static QueryRunPage Unavailable(string query, string message) => new(query, "", message);
}
