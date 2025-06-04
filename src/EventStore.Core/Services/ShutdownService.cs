using System;
using System.Collections.Generic;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.TimerService;
using Serilog;

namespace EventStore.Core.Services;

public class ShutdownService(IPublisher mainQueue, VNodeInfo nodeInfo, TimeSpan? shutdownTimeout = null) :
	IHandle<ClientMessage.RequestShutdown>,
	IHandle<SystemMessage.RegisterForGracefulTermination>,
	IHandle<SystemMessage.ComponentTerminated>,
	IHandle<SystemMessage.PeripheralShutdownTimeout>
{

	enum State
	{
		Running,
		ShuttingDownPeriphery,
		ShuttingDownCore,
	}

	private State _state;

	private static readonly ILogger Log = Serilog.Log.ForContext<ShutdownService>();
	private static readonly TimeSpan PeripheryShutdownTimeout = TimeSpan.FromSeconds(30);

	private readonly Dictionary<string, Action> _shutdownActions = [];
	private readonly TimeSpan _shutdownTimeout = shutdownTimeout ?? PeripheryShutdownTimeout;

	private bool _exitProcess;
	private bool _shutdownHttp;

	private void ShutDownInternalCore()
	{
		_state = State.ShuttingDownCore;
		mainQueue.Publish(new SystemMessage.BecomeShuttingDown(
			correlationId: Guid.NewGuid(),
			exitProcess: _exitProcess,
			shutdownHttp: _shutdownHttp));
	}

	public void Handle(SystemMessage.RegisterForGracefulTermination message)
	{
		if (_state is not State.Running)
		{
			Log.Warning(
				"Component {ComponentName} tried to register for graceful shutdown while the server is shutting down",
				message.ComponentName);
			return;
		}

		if (!_shutdownActions.TryAdd(message.ComponentName, message.Action))
			throw new InvalidOperationException($"Component {message.ComponentName} already registered");

		Log.Information("========== [{HttpEndPoint}] Component '{Component}' is registered for graceful termination",
			nodeInfo.HttpEndPoint,
			message.ComponentName);
	}

	public void Handle(ClientMessage.RequestShutdown message)
	{
		if (_state is not State.Running)
		{
			Log.Debug("Ignored request shutdown message because the server is already shutting down");
			return;
		}

		_exitProcess = message.ExitProcess;
		_shutdownHttp = message.ShutdownHttp;

		if (_shutdownActions.Count == 0)
		{
			ShutDownInternalCore();
			return;
		}

		_state = State.ShuttingDownPeriphery;
		Log.Information("========== [{httpEndPoint}] Is shutting down peripheral components", nodeInfo.HttpEndPoint);

		foreach (var entry in _shutdownActions)
		{
			try
			{
				entry.Value();
			}
			catch (Exception e)
			{
				Log.Warning(e, "Component {ComponentName} faulted when initiating shutdown", entry.Key);
			}
		}

		mainQueue.Publish(TimerMessage.Schedule.Create(
			_shutdownTimeout,
			mainQueue,
			new SystemMessage.PeripheralShutdownTimeout()));
	}

	public void Handle(SystemMessage.ComponentTerminated message)
	{
		if (!_shutdownActions.Remove(message.ComponentName))
			throw new InvalidOperationException($"Component {message.ComponentName} already terminated");

		Log.Information("========== [{HttpEndPoint}] Component '{ComponentName}' has shut down.", nodeInfo.HttpEndPoint,
			message.ComponentName);

		if (_state is not State.ShuttingDownPeriphery || _shutdownActions.Count != 0) return;

		Log.Information("========== [{HttpEndPoint}] All Components Shutdown.", nodeInfo.HttpEndPoint);
		ShutDownInternalCore();
	}

	public void Handle(SystemMessage.PeripheralShutdownTimeout message)
	{
		if (_state is not State.ShuttingDownPeriphery)
		{
			Log.Debug("Ignored shutdown timeout message when in state {State}", _state);
			return;
		}

		Log.Information("========== [{httpEndPoint}] Timed out shutting down peripheral components {Components}",
			nodeInfo.HttpEndPoint,
			string.Join(", ", _shutdownActions.Keys));
		ShutDownInternalCore();
	}
}
