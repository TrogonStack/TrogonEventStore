using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.DataStructures;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.UserManagement;
using EventStore.Plugins.Authentication;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Authentication.InternalAuthentication;

public class InternalAuthenticationProvider : AuthenticationProviderBase, IHandle<InternalAuthenticationProviderMessages.ResetPasswordCache>, ISessionAuthenticationProvider
{
	public const string SessionSecurityStampClaimType = "es:session-security-stamp";
	static readonly ILogger Logger = Serilog.Log.ForContext<InternalAuthenticationProvider>();

	readonly IODispatcher _ioDispatcher;
	readonly bool _logFailedAuthenticationAttempts;
	readonly PasswordHashAlgorithm _passwordHashAlgorithm;
	readonly PasswordAuthenticationLimiter _passwordAuthenticationLimiter;

	readonly LRUCache<string, (string hash, string salt, ClaimsPrincipal principal)> _userPasswordsCache;

	readonly TaskCompletionSource<bool> _tcs = new();

	public InternalAuthenticationProvider(
		ISubscriber subscriber, IODispatcher ioDispatcher,
		PasswordHashAlgorithm passwordHashAlgorithm,
		int cacheSize, bool logFailedAuthenticationAttempts,
		ClusterVNodeOptions.DefaultUserOptions defaultUserOptions,
		ClusterVNodeOptions.PasswordAuthenticationOptions passwordAuthenticationOptions = null
	) : base(name: "internal", diagnosticsName: "InternalAuthentication")
	{
		_ioDispatcher = ioDispatcher;
		_passwordHashAlgorithm = passwordHashAlgorithm;
		_userPasswordsCache = new LRUCache<string, (string, string, ClaimsPrincipal)>("UserPasswords", cacheSize);
		_logFailedAuthenticationAttempts = logFailedAuthenticationAttempts;
		_passwordAuthenticationLimiter = new(passwordAuthenticationOptions ?? new());
		subscriber.Subscribe<SystemMessage.BecomeShutdown>(new ShutdownHandler(_passwordAuthenticationLimiter));

		var userManagement = new UserManagementService(
			ioDispatcher: ioDispatcher,
			passwordHashAlgorithm: _passwordHashAlgorithm,
			skipInitializeStandardUsersCheck: false,
			tcs: _tcs,
			defaultUserOptions: defaultUserOptions
		);

		subscriber.Subscribe<UserManagementMessage.Create>(userManagement);
		subscriber.Subscribe<UserManagementMessage.Update>(userManagement);
		subscriber.Subscribe<UserManagementMessage.Enable>(userManagement);
		subscriber.Subscribe<UserManagementMessage.Disable>(userManagement);
		subscriber.Subscribe<UserManagementMessage.Delete>(userManagement);
		subscriber.Subscribe<UserManagementMessage.ResetPassword>(userManagement);
		subscriber.Subscribe<UserManagementMessage.ChangePassword>(userManagement);
		subscriber.Subscribe<UserManagementMessage.Get>(userManagement);
		subscriber.Subscribe<UserManagementMessage.GetAll>(userManagement);
		subscriber.Subscribe<SystemMessage.BecomeLeader>(userManagement);
		subscriber.Subscribe<SystemMessage.BecomeFollower>(userManagement);
		subscriber.Subscribe<SystemMessage.BecomeReadOnlyReplica>(userManagement);
	}

	public void Handle(InternalAuthenticationProviderMessages.ResetPasswordCache message) =>
		_userPasswordsCache.Remove(message.LoginName);

	public override void Authenticate(AuthenticationRequest authenticationRequest) => Authenticate(authenticationRequest, useCache: true);

	public void AuthenticateSession(AuthenticationRequest authenticationRequest) => Authenticate(authenticationRequest, useCache: false);

	void Authenticate(AuthenticationRequest authenticationRequest, bool useCache)
	{
		var lease = authenticationRequest.HasValidClientCertificate ? null : _passwordAuthenticationLimiter.TryAcquire();
		if (!authenticationRequest.HasValidClientCertificate && lease is null)
		{
			authenticationRequest.NotReady();
			return;
		}

		try
		{
			if (useCache && _userPasswordsCache.TryGet(authenticationRequest.Name, out var cached))
			{
				AuthenticateCached(authenticationRequest, cached.hash, cached.salt, cached.principal);
			}
			else
			{
				var handler = new AuthReadResponseHandler(this, authenticationRequest, lease);
				_ioDispatcher.ReadBackward($"$user-{authenticationRequest.Name}", -1, 1, false,
					SystemAccounts.System, handler, Guid.NewGuid());
				lease = null;
			}
		}
		finally
		{
			lease?.Dispose();
		}
	}

	sealed class ShutdownHandler(PasswordAuthenticationLimiter limiter) : IHandle<SystemMessage.BecomeShutdown>
	{
		public void Handle(SystemMessage.BecomeShutdown message) => limiter.Dispose();
	}

	public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Basic", "UserCertificate"];

	void AuthenticateUncached(AuthenticationRequest authenticationRequest, UserData userData, Guid userEventId)
	{
		if (!AuthenticateImpl(authenticationRequest, userData.Hash, userData.Salt))
		{
			authenticationRequest.Unauthorized();
			return;
		}

		var principal = CreatePrincipal(userData, userEventId);
		CachePassword(authenticationRequest.Name, userData.Hash, userData.Salt, principal);
		authenticationRequest.Authenticated(principal);
	}

	static ClaimsPrincipal CreatePrincipal(UserData userData, Guid userEventId)
	{
		var claims = userData.Groups
			.Select(role => new Claim(ClaimTypes.Role, role))
			.Prepend(new(ClaimTypes.Name, userData.LoginName))
			.Append(new Claim(SessionSecurityStampClaimType, userEventId.ToString("N")))
			.ToList();

		return new(new ClaimsIdentity(claims, "ES-Legacy"));
	}

	public async Task<ClaimsPrincipal> ValidateSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
	{
		var loginName = principal?.Identity?.Name;
		var stamp = principal?.FindFirst(SessionSecurityStampClaimType)?.Value;
		if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(loginName) ||
			!Guid.TryParseExact(stamp, "N", out var userEventId) || userEventId == Guid.Empty || cancellationToken.IsCancellationRequested)
		{
			return null;
		}

		var completion = new TaskCompletionSource<ClaimsPrincipal>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ioDispatcher.ReadBackward($"$user-{loginName}", -1, 1, false, SystemAccounts.System,
			new SessionReadResponseHandler(loginName, userEventId, completion), Guid.NewGuid());
		try
		{
			return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		}
		catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
		{
			return null;
		}
	}

	sealed class SessionReadResponseHandler(string loginName, Guid userEventId, TaskCompletionSource<ClaimsPrincipal> completion)
		: IReadStreamEventsBackwardHandler
	{
		public bool HandlesAlt => true;
		public bool HandlesTimeout => true;

		public void Handle(ClientMessage.ReadStreamEventsBackwardCompleted completed)
		{
			ClaimsPrincipal principal = null;
			try
			{
				if (completed.Result == ReadStreamResult.Success && completed.Events.Count == 1 &&
					completed.Events[0].Event.EventId == userEventId)
				{
					var userData = completed.Events[0].Event.Data.ParseJson<UserData>();
					if (userData.LoginName == loginName && !userData.Disabled)
					{
						principal = new ClaimsPrincipal(new LocalSessionClaimsIdentity(CreatePrincipal(userData, userEventId).Claims));
					}
				}
			}
			catch
			{
				principal = null;
			}

			completion.TrySetResult(principal);
		}

		public void Handle(ClientMessage.NotHandled notHandled) => completion.TrySetResult(null);
		public void Timeout() => completion.TrySetResult(null);
	}

	void CachePassword(string loginName, string hash, string salt, ClaimsPrincipal principal) =>
		_userPasswordsCache.Put(loginName, (hash, salt, principal));

	void AuthenticateCached(AuthenticationRequest authenticationRequest, string passwordHash, string passwordSalt, ClaimsPrincipal principal)
	{
		if (!AuthenticateImpl(authenticationRequest, passwordHash, passwordSalt))
		{
			authenticationRequest.Unauthorized();
			return;
		}

		authenticationRequest.Authenticated(principal);
	}

	bool AuthenticateImpl(AuthenticationRequest authenticationRequest, string passwordHash, string passwordSalt)
	{
		if (authenticationRequest.HasValidClientCertificate)
		{
			// a valid user certificate was supplied. we only needed to verify if the certificate's user
			// exists and is enabled, which we have.
			return true;
		}

		// otherwise default to password authentication
		if (_passwordHashAlgorithm.Verify(authenticationRequest.SuppliedPassword, passwordHash, passwordSalt))
		{
			return true;
		}

		if (_logFailedAuthenticationAttempts)
		{
			Logger.Warning("Authentication Failed for {Id}: {Reason}", authenticationRequest.Id, "Invalid credentials supplied.");
		}

		return false;
	}

	public override Task Initialize() => _tcs.Task;

	class AuthReadResponseHandler(InternalAuthenticationProvider self, AuthenticationRequest request, IDisposable lease) : IReadStreamEventsBackwardHandler
	{
		int _completed;
		public bool HandlesAlt => true;
		public bool HandlesTimeout => true;

		public void Handle(ClientMessage.ReadStreamEventsBackwardCompleted completed)
		{
			if (Interlocked.Exchange(ref _completed, 1) != 0)
				return;
			try
			{
				if (completed.Result == ReadStreamResult.StreamDeleted ||
					completed.Result == ReadStreamResult.NoStream ||
					completed.Result == ReadStreamResult.AccessDenied)
				{
					if (self._logFailedAuthenticationAttempts)
					{
						Logger.Warning("Authentication Failed for {Id}: {Reason}", request.Id, "Invalid user.");
					}

					request.Unauthorized();
					return;
				}

				if (completed.Result == ReadStreamResult.Error)
				{
					if (self._logFailedAuthenticationAttempts)
					{
						Logger.Warning("Authentication Failed for {Id}: {Reason}", request.Id, "Unexpected error.");
					}

					request.Error();
					return;
				}

				var userData = completed.Events[0].Event.Data.ParseJson<UserData>();
				if (userData.LoginName != request.Name)
				{
					request.Error();
					return;
				}

				if (userData.Disabled)
				{
					if (self._logFailedAuthenticationAttempts)
					{
						Logger.Warning("Authentication Failed for {Id}: {Reason}", request.Id, "The account is disabled.");
					}

					request.Unauthorized();
				}
				else
				{
					self.AuthenticateUncached(request, userData, completed.Events[0].Event.EventId);
				}
			}
			catch
			{
				request.Unauthorized();
			}
			finally
			{
				lease?.Dispose();
			}
		}

		public void Handle(ClientMessage.NotHandled notHandled)
		{
			if (Interlocked.Exchange(ref _completed, 1) != 0)
				return;
			using var acquired = lease;
			if (self._logFailedAuthenticationAttempts)
			{
				Logger.Warning(
					"Authentication Failed for {Id}: {Reason}. {Description}",
					request.Id, notHandled.Reason, notHandled.Description
				);
			}

			request.NotReady();
		}

		public void Timeout()
		{
			if (Interlocked.Exchange(ref _completed, 1) != 0)
				return;
			using var acquired = lease;
			if (self._logFailedAuthenticationAttempts)
			{
				Logger.Warning("Authentication Failed for {Id}: {Reason}", request.Id, "Timeout.");
			}

			request.NotReady();
		}
	}
}
