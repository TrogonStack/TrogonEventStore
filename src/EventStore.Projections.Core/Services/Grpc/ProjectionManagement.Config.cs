using System.Threading.Tasks;
using EventStore.Client.Projections;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using EventStore.Projections.Core.Messages;
using Grpc.Core;

namespace EventStore.Projections.Core.Services.Grpc;

internal partial class ProjectionManagement
{
	private static readonly Operation ReadConfigurationOperation = new Operation(Operations.Projections.ReadConfiguration);
	private static readonly Operation UpdateConfigurationOperation = new Operation(Operations.Projections.UpdateConfiguration);

	public override async Task<GetConfigResp> GetConfig(GetConfigReq request, ServerCallContext context)
	{
		var configSource = new TaskCompletionSource<ProjectionManagementMessage.ProjectionConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var getConfigCancellationRegistration =
			context.CancellationToken.Register(() => configSource.TrySetCanceled(context.CancellationToken));
		var name = request.Options.Name;
		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user, ReadConfigurationOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var envelope = new CallbackEnvelope(OnMessage);
		_publisher.Publish(new ProjectionManagementMessage.Command.GetConfig(
			envelope,
			name,
			new ProjectionManagementMessage.RunAs(user)));

		var config = await configSource.Task;
		var details = new GetConfigResp.Types.Details
		{
			EmitEnabled = config.EmitEnabled,
			TrackEmittedStreams = config.TrackEmittedStreams,
			CheckpointAfterMs = config.CheckpointAfterMs,
			CheckpointHandledThreshold = config.CheckpointHandledThreshold,
			CheckpointUnhandledBytesThreshold = config.CheckpointUnhandledBytesThreshold,
			PendingEventsThreshold = config.PendingEventsThreshold,
			MaxWriteBatchLength = config.MaxWriteBatchLength,
			MaxAllowedWritesInFlight = config.MaxAllowedWritesInFlight
		};
		if (config.ProjectionExecutionTimeout is { } projectionExecutionTimeout)
		{
			details.ProjectionExecutionTimeout = projectionExecutionTimeout;
		}

		return new GetConfigResp
		{
			Details = details
		};

		void OnMessage(Message message)
		{
			switch (message)
			{
				case ProjectionManagementMessage.ProjectionConfig projectionConfig:
					configSource.TrySetResult(projectionConfig);
					break;
				case ProjectionManagementMessage.NotFound:
					configSource.TrySetException(ProjectionNotFound(name));
					break;
				default:
					configSource.TrySetException(UnknownMessage<ProjectionManagementMessage.ProjectionConfig>(message));
					break;
			}
		}
	}

	public override async Task<UpdateConfigResp> UpdateConfig(UpdateConfigReq request, ServerCallContext context)
	{
		var updateSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var updateConfigCancellationRegistration =
			context.CancellationToken.Register(() => updateSource.TrySetCanceled(context.CancellationToken));
		var options = request.Options;
		var name = options.Name;
		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user, UpdateConfigurationOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var envelope = new CallbackEnvelope(OnMessage);
		_publisher.Publish(new ProjectionManagementMessage.Command.UpdateConfig(
			envelope,
			name,
			options.EmitEnabled,
			options.TrackEmittedStreams,
			options.CheckpointAfterMs,
			options.CheckpointHandledThreshold,
			options.CheckpointUnhandledBytesThreshold,
			options.PendingEventsThreshold,
			options.MaxWriteBatchLength,
			options.MaxAllowedWritesInFlight,
			new ProjectionManagementMessage.RunAs(user),
			options.HasProjectionExecutionTimeout ? options.ProjectionExecutionTimeout : null));

		await updateSource.Task;
		return new UpdateConfigResp();

		void OnMessage(Message message)
		{
			switch (message)
			{
				case ProjectionManagementMessage.Updated:
					updateSource.TrySetResult(true);
					break;
				case ProjectionManagementMessage.NotFound:
					updateSource.TrySetException(ProjectionNotFound(name));
					break;
				default:
					updateSource.TrySetException(UnknownMessage<ProjectionManagementMessage.Updated>(message));
					break;
			}
		}
	}
}
