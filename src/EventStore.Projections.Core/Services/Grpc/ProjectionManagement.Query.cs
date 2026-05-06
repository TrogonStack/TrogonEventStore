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
	private static readonly Operation ReadOperation = new Operation(Operations.Projections.Read);

	public override async Task<GetQueryResp> GetQuery(GetQueryReq request, ServerCallContext context)
	{
		var querySource = new TaskCompletionSource<ProjectionManagementMessage.ProjectionQuery>();
		var name = request.Options.Name;
		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user, ReadOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var envelope = new CallbackEnvelope(OnMessage);
		_publisher.Publish(new ProjectionManagementMessage.Command.GetQuery(
			envelope,
			name,
			new ProjectionManagementMessage.RunAs(user)));

		var query = await querySource.Task;
		var details = new GetQueryResp.Types.Details {
			Name = query.Name ?? string.Empty,
			Query = query.Query ?? string.Empty,
			EmitEnabled = query.EmitEnabled,
			ProjectionType = query.Type ?? string.Empty,
			DefinitionJson = ToJson(query.Definition),
			OutputConfigJson = ToJson(query.OutputConfig)
		};
		if (query.TrackEmittedStreams is { } trackEmittedStreams)
		{
			details.TrackEmittedStreams = trackEmittedStreams;
		}

		if (query.CheckpointsEnabled is { } checkpointsEnabled)
		{
			details.CheckpointsEnabled = checkpointsEnabled;
		}

		return new GetQueryResp {
			Details = details
		};

		void OnMessage(Message message)
		{
			switch (message)
			{
				case ProjectionManagementMessage.ProjectionQuery projectionQuery:
					querySource.TrySetResult(projectionQuery);
					break;
				case ProjectionManagementMessage.NotFound:
					querySource.TrySetException(ProjectionNotFound(name));
					break;
				default:
					querySource.TrySetException(UnknownMessage<ProjectionManagementMessage.ProjectionQuery>(message));
					break;
			}
		}
	}
}
