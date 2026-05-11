using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Users;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc;

internal partial class Users
{
	private static readonly Operation ReadOperation = new Operation(Plugins.Authorization.Operations.Users.Read);
	private static readonly Operation ListOperation = new Operation(Plugins.Authorization.Operations.Users.List);
	public override async Task Details(DetailsReq request, IServerStreamWriter<DetailsResp> responseStream,
		ServerCallContext context)
	{
		var options = request.Options;
		var loginName = options?.LoginName;

		var user = context.GetHttpContext().User;
		var operation = string.IsNullOrWhiteSpace(loginName)
			? ListOperation
			: ReadOperation.WithParameter(Plugins.Authorization.Operations.Users.Parameters.User(loginName));
		if (!await _authorizationProvider.CheckAccessAsync(user, operation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}
		var detailsSource = new TaskCompletionSource<UserManagementMessage.UserData[]>(TaskCreationOptions.RunContinuationsAsynchronously);

		var envelope = new CallbackEnvelope(OnMessage);

		_publisher.Publish(string.IsNullOrWhiteSpace(loginName)
			? (Message)new UserManagementMessage.GetAll(envelope, user)
			: new UserManagementMessage.Get(envelope, user, loginName));

		var details = await detailsSource.Task.WaitAsync(context.CancellationToken);

		foreach (var detail in details)
		{
			await responseStream.WriteAsync(new DetailsResp
			{
				UserDetails = ToUserDetails(detail)
			});
		}

		void OnMessage(Message message)
		{
			if (HandleErrors(loginName, message, detailsSource))
			{
				return;
			}

			switch (message)
			{
				case UserManagementMessage.UserDetailsResult userDetails:
					detailsSource.TrySetResult(new[] { userDetails.Data });
					break;
				case UserManagementMessage.AllUserDetailsResult allUserDetails:
					detailsSource.TrySetResult(allUserDetails.Data);
					break;
				default:
					detailsSource.TrySetException(RpcExceptions.UnknownError(1));
					break;
			}
		}
	}

	private static DetailsResp.Types.UserDetails ToUserDetails(UserManagementMessage.UserData detail) =>
		new()
		{
			Disabled = detail.Disabled,
			Groups = { detail.Groups },
			FullName = detail.FullName,
			LoginName = detail.LoginName,
			LastUpdated = detail.DateLastUpdated.HasValue
				? new DetailsResp.Types.UserDetails.Types.DateTime { TicksSinceEpoch = detail.DateLastUpdated.Value.UtcDateTime.ToTicksSinceEpoch() }
				: null
		};
}
