using System;
using System.Threading.Tasks;
using EventStore.Client.Projections;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using EventStore.Projections.Core.Messages;
using Grpc.Core;
using static EventStore.Client.Projections.CreateReq.Types.Options;

namespace EventStore.Projections.Core.Services.Grpc;

internal partial class ProjectionManagement {
	private static readonly Operation CreateOperation = new Operation(Operations.Projections.Create);

	public override async Task<CreateResp> Create(CreateReq request, ServerCallContext context) {
		var createdSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var options = request.Options;

		var user = context.GetHttpContext().User;
		if (!await _authorizationProvider.CheckAccessAsync(user, CreateOperation, context.CancellationToken)) {
			throw RpcExceptions.AccessDenied();
		}
		var handlerType = string.IsNullOrWhiteSpace(options.HandlerType) ? "JS" : options.HandlerType;
		var name = options.ModeCase switch {
			ModeOneofCase.Continuous => options.Continuous.Name,
			ModeOneofCase.Transient => options.Transient.Name,
			ModeOneofCase.OneTime => string.IsNullOrWhiteSpace(options.Name)
				? Guid.NewGuid().ToString("D")
				: options.Name,
			_ => throw new InvalidOperationException()
		};
		var projectionMode = options.ModeCase switch {
			ModeOneofCase.Continuous => ProjectionMode.Continuous,
			ModeOneofCase.Transient => ProjectionMode.Transient,
			ModeOneofCase.OneTime => ProjectionMode.OneTime,
			_ => throw new InvalidOperationException()
		};
		var emitEnabled = options.ModeCase switch {
			ModeOneofCase.OneTime => options.EmitEnabled,
			ModeOneofCase.Continuous => options.Continuous.EmitEnabled,
			_ => false
		};
		var trackEmittedStreams = (options.ModeCase, emitEnabled) switch {
			(ModeOneofCase.OneTime, true) when options.TrackEmittedStreams => true,
			(ModeOneofCase.Continuous, true) when options.Continuous.TrackEmittedStreams => true,
			_ => false
		};
		var checkpointsEnabled = options.ModeCase switch {
			ModeOneofCase.Continuous => true,
			ModeOneofCase.OneTime => options.CheckpointsEnabled,
			ModeOneofCase.Transient => false,
			_ => throw new InvalidOperationException()
		};
		var enabled = (options.EnabledOptionCase, options.Enabled) switch {
			(EnabledOptionOneofCase.Enabled, true) => true,
			(EnabledOptionOneofCase.Enabled, false) => false,
			(EnabledOptionOneofCase.NoEnabledOption, _) => true,
			(EnabledOptionOneofCase.None, _) => true,
			_ => throw new InvalidOperationException()
		};

		var runAs = new ProjectionManagementMessage.RunAs(user);

		var envelope = new CallbackEnvelope(OnMessage);

		_publisher.Publish(new ProjectionManagementMessage.Command.Post(envelope, projectionMode, name, runAs,
			handlerType, options.Query, enabled, checkpointsEnabled, emitEnabled, trackEmittedStreams, true));

		await createdSource.Task;

		return new CreateResp();

		void OnMessage(Message message) {
			if (message is not ProjectionManagementMessage.Updated) {
				createdSource.TrySetException(UnknownMessage<ProjectionManagementMessage.Updated>(message));
				return;
			}

			createdSource.TrySetResult(true);
		}
	}
}
