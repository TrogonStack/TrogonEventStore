using System;
using System.Threading.Tasks;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc;

internal partial class PersistentSubscriptions
{
	public override async Task<TruncateParkedResp> TruncateParked(TruncateParkedReq request, ServerCallContext context)
	{
		var truncateParkedMessagesSource = new TaskCompletionSource<TruncateParkedResp>(TaskCreationOptions.RunContinuationsAsynchronously);
		var correlationId = Guid.NewGuid();

		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user,
			ReplayParkedOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		string streamId = request.Options.StreamOptionCase switch
		{
			TruncateParkedReq.Types.Options.StreamOptionOneofCase.All => "$all",
			TruncateParkedReq.Types.Options.StreamOptionOneofCase.StreamIdentifier => request.Options.StreamIdentifier,
			_ => throw new InvalidOperationException()
		};

		long? stopAt = request.Options.StopAtOptionCase switch
		{
			TruncateParkedReq.Types.Options.StopAtOptionOneofCase.StopAt => request.Options.StopAt,
			TruncateParkedReq.Types.Options.StopAtOptionOneofCase.NoLimit => null,
			_ => throw new InvalidOperationException()
		};

		_publisher.Publish(new ClientMessage.TruncateParkedMessages(
			correlationId,
			correlationId,
			new CallbackEnvelope(HandleTruncateParkedMessagesCompleted),
			streamId,
			request.Options.GroupName,
			stopAt,
			user));
		return await truncateParkedMessagesSource.Task;

		void HandleTruncateParkedMessagesCompleted(Message message)
		{
			if (message is ClientMessage.NotHandled notHandled && RpcExceptions.TryHandleNotHandled(notHandled, out var ex))
			{
				truncateParkedMessagesSource.TrySetException(ex);
				return;
			}

			if (message is ClientMessage.TruncateParkedMessagesCompleted completed)
			{
				switch (completed.Result)
				{
					case ClientMessage.TruncateParkedMessagesCompleted.TruncateParkedMessagesResult.Success:
						truncateParkedMessagesSource.TrySetResult(new TruncateParkedResp());
						return;
					case ClientMessage.TruncateParkedMessagesCompleted.TruncateParkedMessagesResult.DoesNotExist:
						truncateParkedMessagesSource.TrySetException(
							RpcExceptions.PersistentSubscriptionDoesNotExist(streamId,
								request.Options.GroupName));
						return;
					case ClientMessage.TruncateParkedMessagesCompleted.TruncateParkedMessagesResult.AccessDenied:
						truncateParkedMessagesSource.TrySetException(
							RpcExceptions.AccessDenied());
						return;
					case ClientMessage.TruncateParkedMessagesCompleted.TruncateParkedMessagesResult.Fail:
						truncateParkedMessagesSource.TrySetException(
							RpcExceptions.PersistentSubscriptionFailed(streamId, request.Options.GroupName,
								completed.Reason));
						return;

					default:
						truncateParkedMessagesSource.TrySetException(
							RpcExceptions.UnknownError(completed.Result));
						return;
				}
			}
			truncateParkedMessagesSource.TrySetException(
				RpcExceptions.UnknownMessage<ClientMessage.TruncateParkedMessagesCompleted>(
					message));
		}
	}
}
