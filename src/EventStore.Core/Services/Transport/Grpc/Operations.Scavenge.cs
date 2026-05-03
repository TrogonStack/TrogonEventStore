using System;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Operations;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc {
	partial class Operations {
		private static readonly Operation StartOperation = new(Plugins.Authorization.Operations.Node.Scavenge.Start);
		private static readonly Operation StopOperation = new(Plugins.Authorization.Operations.Node.Scavenge.Stop);
		private static readonly Operation ReadOperation = new(Plugins.Authorization.Operations.Node.Scavenge.Read);

		public override async Task<ScavengeResp> StartScavenge(StartScavengeReq request, ServerCallContext context) {
			var scavengeResultSource =
				new TaskCompletionSource<(string, ScavengeResp.Types.ScavengeResult)>(
					TaskCreationOptions.RunContinuationsAsynchronously);

			var user = context.GetHttpContext().User;
			if (!await _authorizationProvider.CheckAccessAsync(user, StartOperation, context.CancellationToken))
				throw RpcExceptions.AccessDenied();

			var options = request.Options ?? new StartScavengeReq.Types.Options();
			var threads = options.ThreadCount == 0 ? 1 : options.ThreadCount;
			if (options.StartFromChunk < 0)
				throw new RpcException(new Status(StatusCode.InvalidArgument, "start_from_chunk must be a non-negative integer"));
			if (threads < 1)
				throw new RpcException(new Status(StatusCode.InvalidArgument, "thread_count must be 1 or above"));
			if (options.HasThrottlePercent) {
				if (options.ThrottlePercent is <= 0 or > 100)
					throw new RpcException(new Status(
						StatusCode.InvalidArgument,
						"throttle_percent must be between 1 and 100 inclusive"));
				if (options.ThrottlePercent != 100 && threads > 1)
					throw new RpcException(new Status(
						StatusCode.InvalidArgument,
						"throttle_percent must be 100 for a multi-threaded scavenge"));
			}

			_publisher.Publish(new ClientMessage.ScavengeDatabase(
				new CallbackEnvelope(OnMessage),
				Guid.NewGuid(),
				user,
				options.StartFromChunk,
				threads,
				options.HasThreshold ? options.Threshold : null,
				options.HasThrottlePercent ? options.ThrottlePercent : null,
				options.SyncOnly));

			var (scavengeId, scavengeResult) = await scavengeResultSource.Task.WaitAsync(context.CancellationToken);

			return new ScavengeResp {
				ScavengeId = scavengeId,
				ScavengeResult = scavengeResult
			};

			void OnMessage(Message message) => HandleScavengeDatabaseResponse(message, scavengeResultSource);
		}

		public override async Task<ScavengeResp> StopScavenge(StopScavengeReq request, ServerCallContext context) {
			var scavengeResultSource =
				new TaskCompletionSource<(string, ScavengeResp.Types.ScavengeResult)>(
					TaskCreationOptions.RunContinuationsAsynchronously);

			var user = context.GetHttpContext().User;
			if (!await _authorizationProvider.CheckAccessAsync(user, StopOperation, context.CancellationToken))
				throw RpcExceptions.AccessDenied();

			var options = request.Options;
			if (options is null || string.IsNullOrWhiteSpace(options.ScavengeId))
				throw new RpcException(new Status(StatusCode.InvalidArgument, "scavenge_id is required"));

			_publisher.Publish(new ClientMessage.StopDatabaseScavenge(
				new CallbackEnvelope(OnMessage),
				Guid.NewGuid(),
				user,
				options.ScavengeId));

			var (scavengeId, scavengeResult) = await scavengeResultSource.Task.WaitAsync(context.CancellationToken);

			return new ScavengeResp {
				ScavengeId = scavengeId,
				ScavengeResult = scavengeResult
			};

			void OnMessage(Message message) => HandleScavengeDatabaseResponse(message, scavengeResultSource);
		}

		public override async Task<ScavengeStatusResp> GetCurrentScavenge(
			Empty request,
			ServerCallContext context) {
			var scavengeStatusSource =
				new TaskCompletionSource<ScavengeStatusResp>(TaskCreationOptions.RunContinuationsAsynchronously);

			var user = context.GetHttpContext().User;
			if (!await _authorizationProvider.CheckAccessAsync(user, ReadOperation, context.CancellationToken))
				throw RpcExceptions.AccessDenied();

			_publisher.Publish(new ClientMessage.GetCurrentDatabaseScavenge(
				new CallbackEnvelope(OnMessage),
				Guid.NewGuid(),
				user));

			return await scavengeStatusSource.Task.WaitAsync(context.CancellationToken);

			void OnMessage(Message message) => HandleCurrentScavengeResponse(message, scavengeStatusSource);
		}

		public override async Task<ScavengeStatusResp> GetLastScavenge(Empty request, ServerCallContext context) {
			var scavengeStatusSource =
				new TaskCompletionSource<ScavengeStatusResp>(TaskCreationOptions.RunContinuationsAsynchronously);

			var user = context.GetHttpContext().User;
			if (!await _authorizationProvider.CheckAccessAsync(user, ReadOperation, context.CancellationToken))
				throw RpcExceptions.AccessDenied();

			_publisher.Publish(new ClientMessage.GetLastDatabaseScavenge(
				new CallbackEnvelope(OnMessage),
				Guid.NewGuid(),
				user));

			return await scavengeStatusSource.Task.WaitAsync(context.CancellationToken);

			void OnMessage(Message message) => HandleLastScavengeResponse(message, scavengeStatusSource);
		}

		private static void HandleScavengeDatabaseResponse(
			Message message,
			TaskCompletionSource<(string, ScavengeResp.Types.ScavengeResult)> scavengeResultSource) {
			switch (message) {
				case ClientMessage.ScavengeDatabaseUnauthorizedResponse:
					scavengeResultSource.TrySetException(RpcExceptions.AccessDenied());
					return;
				case ClientMessage.ScavengeDatabaseNotFoundResponse notFoundResponse:
					scavengeResultSource.TrySetException(RpcExceptions.ScavengeNotFound(notFoundResponse.ScavengeId));
					return;
				case ClientMessage.ScavengeDatabaseStartedResponse startedResponse:
					scavengeResultSource.TrySetResult((startedResponse.ScavengeId,
						ScavengeResp.Types.ScavengeResult.Started));
					return;
				case ClientMessage.ScavengeDatabaseStoppedResponse stoppedResponse:
					scavengeResultSource.TrySetResult((stoppedResponse.ScavengeId,
						ScavengeResp.Types.ScavengeResult.Stopped));
					return;
				case ClientMessage.ScavengeDatabaseInProgressResponse inProgressResponse:
					scavengeResultSource.TrySetResult((inProgressResponse.ScavengeId,
						ScavengeResp.Types.ScavengeResult.InProgress));
					return;
				default:
					scavengeResultSource.TrySetException(RpcExceptions.UnknownMessage<Message>(message));
					return;
			}
		}

		private static void HandleCurrentScavengeResponse(
			Message message,
			TaskCompletionSource<ScavengeStatusResp> scavengeStatusSource) {
			switch (message) {
				case ClientMessage.ScavengeDatabaseUnauthorizedResponse:
					scavengeStatusSource.TrySetException(RpcExceptions.AccessDenied());
					return;
				case ClientMessage.ScavengeDatabaseGetCurrentResponse currentResponse:
					scavengeStatusSource.TrySetResult(new ScavengeStatusResp {
						ScavengeId = currentResponse.ScavengeId ?? string.Empty,
						ScavengeStatus = currentResponse.Result switch {
							ClientMessage.ScavengeDatabaseGetCurrentResponse.ScavengeResult.InProgress =>
								ScavengeStatusResp.Types.ScavengeStatus.InProgress,
							ClientMessage.ScavengeDatabaseGetCurrentResponse.ScavengeResult.Stopped =>
								ScavengeStatusResp.Types.ScavengeStatus.Stopped,
							_ => ScavengeStatusResp.Types.ScavengeStatus.Unknown
						}
					});
					return;
				default:
					scavengeStatusSource.TrySetException(
						RpcExceptions.UnknownMessage<ClientMessage.ScavengeDatabaseGetCurrentResponse>(message));
					return;
			}
		}

		private static void HandleLastScavengeResponse(
			Message message,
			TaskCompletionSource<ScavengeStatusResp> scavengeStatusSource) {
			switch (message) {
				case ClientMessage.ScavengeDatabaseUnauthorizedResponse:
					scavengeStatusSource.TrySetException(RpcExceptions.AccessDenied());
					return;
				case ClientMessage.ScavengeDatabaseGetLastResponse lastResponse:
					scavengeStatusSource.TrySetResult(new ScavengeStatusResp {
						ScavengeId = lastResponse.ScavengeId ?? string.Empty,
						ScavengeStatus = lastResponse.Result switch {
							ClientMessage.ScavengeDatabaseGetLastResponse.ScavengeResult.Unknown =>
								ScavengeStatusResp.Types.ScavengeStatus.Unknown,
							ClientMessage.ScavengeDatabaseGetLastResponse.ScavengeResult.InProgress =>
								ScavengeStatusResp.Types.ScavengeStatus.InProgress,
							ClientMessage.ScavengeDatabaseGetLastResponse.ScavengeResult.Stopped =>
								ScavengeStatusResp.Types.ScavengeStatus.Stopped,
							ClientMessage.ScavengeDatabaseGetLastResponse.ScavengeResult.Success =>
								ScavengeStatusResp.Types.ScavengeStatus.Success,
							ClientMessage.ScavengeDatabaseGetLastResponse.ScavengeResult.Errored =>
								ScavengeStatusResp.Types.ScavengeStatus.Errored,
							_ => ScavengeStatusResp.Types.ScavengeStatus.Unknown
						}
					});
					return;
				default:
					scavengeStatusSource.TrySetException(
						RpcExceptions.UnknownMessage<ClientMessage.ScavengeDatabaseGetLastResponse>(message));
					return;
			}
		}
	}
}
