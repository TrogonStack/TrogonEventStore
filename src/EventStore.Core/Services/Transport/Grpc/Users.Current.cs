using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc;

internal partial class Users
{
	private static readonly Operation CurrentUserOperation =
		new Operation(Plugins.Authorization.Operations.Users.CurrentUser);

	public override async Task<CurrentResp> Current(CurrentReq request, ServerCallContext context)
	{
		var user = context.GetHttpContext().User;
		if (!await _authorizationProvider.CheckAccessAsync(user, CurrentUserOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var loginName = user?.Identity?.Name;
		if (string.IsNullOrWhiteSpace(loginName))
		{
			throw RpcExceptions.AccessDenied();
		}

		var detailsSource = new TaskCompletionSource<UserManagementMessage.UserData>(TaskCreationOptions.RunContinuationsAsynchronously);
		var envelope = new CallbackEnvelope(OnMessage);
		_publisher.Publish(new UserManagementMessage.Get(envelope, user, loginName));

		return new CurrentResp
		{
			UserDetails = ToUserDetails(await detailsSource.Task.WaitAsync(context.CancellationToken))
		};

		void OnMessage(Message message)
		{
			if (HandleErrors(loginName, message, detailsSource))
				return;

			switch (message)
			{
				case UserManagementMessage.UserDetailsResult userDetails:
					detailsSource.TrySetResult(userDetails.Data);
					break;
				default:
					detailsSource.TrySetException(RpcExceptions.UnknownError(1));
					break;
			}
		}
	}
}
