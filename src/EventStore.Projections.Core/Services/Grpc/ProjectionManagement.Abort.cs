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
	private static readonly Operation AbortOperation = new Operation(Operations.Projections.Abort);

	public override async Task<AbortResp> Abort(AbortReq request, ServerCallContext context)
	{
		var abortSource = new TaskCompletionSource<bool>();
		var name = request.Options.Name;
		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user, AbortOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var envelope = new CallbackEnvelope(OnMessage);
		_publisher.Publish(new ProjectionManagementMessage.Command.Abort(
			envelope,
			name,
			new ProjectionManagementMessage.RunAs(user)));

		await abortSource.Task;
		return new AbortResp();

		void OnMessage(Message message)
		{
			switch (message)
			{
				case ProjectionManagementMessage.Updated:
					abortSource.TrySetResult(true);
					break;
				case ProjectionManagementMessage.NotFound:
					abortSource.TrySetException(ProjectionNotFound(name));
					break;
				default:
					abortSource.TrySetException(UnknownMessage<ProjectionManagementMessage.Updated>(message));
					break;
			}
		}
	}
}
