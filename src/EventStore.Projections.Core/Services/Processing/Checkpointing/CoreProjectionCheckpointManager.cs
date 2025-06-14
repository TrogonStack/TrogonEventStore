using System;
using System.Diagnostics.Contracts;
using EventStore.Core.Bus;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;
using EventStore.Projections.Core.Services.Processing.Partitioning;
using Serilog;
using ILogger = Serilog.ILogger;

namespace EventStore.Projections.Core.Services.Processing.Checkpointing;

public abstract class CoreProjectionCheckpointManager : IProjectionCheckpointManager,
	ICoreProjectionCheckpointManager,
	IEmittedEventWriter
{
	protected readonly ProjectionNamesBuilder _namingBuilder;
	protected readonly ProjectionConfig _projectionConfig;
	protected readonly ILogger _logger;

	private readonly bool _usePersistentCheckpoints;

	protected readonly IPublisher _publisher;
	private readonly Guid _projectionCorrelationId;
	private readonly CheckpointTag _zeroTag;


	protected ProjectionCheckpoint _currentCheckpoint;
	private ProjectionCheckpoint _closingCheckpoint;
	internal CheckpointTag _requestedCheckpointPosition;
	private bool _inCheckpoint;
	private PartitionState _requestedCheckpointState;
	private CheckpointTag _lastCompletedCheckpointPosition;
	private readonly PositionTracker _lastProcessedEventPosition;
	private float _lastProcessedEventProgress;

	private int _eventsProcessedAfterRestart;
	private bool _started;
	protected bool _stopping;
	protected bool _stopped;

	private PartitionState _currentProjectionState;
	private bool _largeStateWarningLogged = false;

	protected CoreProjectionCheckpointManager(
		IPublisher publisher,
		Guid projectionCorrelationId,
		ProjectionConfig projectionConfig,
		string name,
		PositionTagger positionTagger,
		ProjectionNamesBuilder namingBuilder,
		bool usePersistentCheckpoints)
	{
		if (publisher == null)
			throw new ArgumentNullException("publisher");
		if (projectionConfig == null)
			throw new ArgumentNullException("projectionConfig");
		if (name == null)
			throw new ArgumentNullException("name");
		if (positionTagger == null)
			throw new ArgumentNullException("positionTagger");
		if (namingBuilder == null)
			throw new ArgumentNullException("namingBuilder");
		if (name == "")
			throw new ArgumentException("name");

		_lastProcessedEventPosition = new PositionTracker(positionTagger);
		_zeroTag = positionTagger.MakeZeroCheckpointTag();

		_publisher = publisher;
		_projectionCorrelationId = projectionCorrelationId;
		_projectionConfig = projectionConfig;
		_logger = Serilog.Log.ForContext<CoreProjectionCheckpointManager>();
		_namingBuilder = namingBuilder;
		_usePersistentCheckpoints = usePersistentCheckpoints;
		_requestedCheckpointState = new PartitionState("", null, _zeroTag);
		_currentProjectionState = new PartitionState("", null, _zeroTag);
	}

	protected abstract ProjectionCheckpoint CreateProjectionCheckpoint(CheckpointTag checkpointPosition);

	protected abstract void BeginWriteCheckpoint(
		CheckpointTag requestedCheckpointPosition, string requestedCheckpointState);

	protected abstract void CapturePartitionStateUpdated(string partition, PartitionState oldState,
		PartitionState newState);

	protected abstract void EmitPartitionCheckpoints();

	public abstract void RecordEventOrder(ResolvedEvent resolvedEvent, CheckpointTag orderCheckpointTag,
		Action committed);

	public abstract void BeginLoadPartitionStateAt(
		string statePartition, CheckpointTag requestedStateCheckpointTag, Action<PartitionState> loadCompleted);

	public abstract void PartitionCompleted(string partition);

	public virtual void Initialize()
	{
		if (_currentCheckpoint != null)
			_currentCheckpoint.Dispose();
		if (_closingCheckpoint != null)
			_closingCheckpoint.Dispose();
		_currentCheckpoint = null;
		_closingCheckpoint = null;
		_requestedCheckpointPosition = null;
		_inCheckpoint = false;
		_requestedCheckpointState = new PartitionState("", null, _zeroTag);
		_lastCompletedCheckpointPosition = null;
		_lastProcessedEventPosition.Initialize();
		_lastProcessedEventProgress = -1;

		_eventsProcessedAfterRestart = 0;
		_started = false;
		_stopping = false;
		_stopped = false;
		_currentProjectionState = new PartitionState("", null, _zeroTag);
	}

	public virtual void Start(CheckpointTag checkpointTag, PartitionState rootPartitionState)
	{
		Contract.Requires(_currentCheckpoint == null);
		if (_started)
			throw new InvalidOperationException("Already started");
		_started = true;

		if (rootPartitionState != null)
			_currentProjectionState = rootPartitionState;

		_lastProcessedEventPosition.UpdateByCheckpointTagInitial(checkpointTag);
		_lastProcessedEventProgress = -1;
		_lastCompletedCheckpointPosition = checkpointTag;
		_requestedCheckpointPosition = null;
		_currentCheckpoint = CreateProjectionCheckpoint(_lastProcessedEventPosition.LastTag);
		_currentCheckpoint.Start();
	}

	public void Stopping()
	{
		EnsureStarted();
		if (_stopping)
			throw new InvalidOperationException("Already stopping");
		_stopping = true;
		RequestCheckpointToStop();
	}

	public void Stopped()
	{
		EnsureStarted();
		_started = false;
		_stopped = true;

		if (_currentCheckpoint != null)
			_currentCheckpoint.Dispose();
		_currentCheckpoint = null;

		if (_closingCheckpoint != null)
			_closingCheckpoint.Dispose();
		_closingCheckpoint = null;
	}

	public virtual void GetStatistics(ProjectionStatistics info)
	{
		info.Position = (_lastProcessedEventPosition.LastTag ?? (object)"").ToString();
		info.Progress = _lastProcessedEventProgress;
		info.LastCheckpoint = _lastCompletedCheckpointPosition != null
			? _lastCompletedCheckpointPosition.ToString()
			: "";
		info.EventsProcessedAfterRestart = _eventsProcessedAfterRestart;
		info.WritePendingEventsBeforeCheckpoint = _closingCheckpoint != null
			? _closingCheckpoint.GetWritePendingEvents()
			: 0;
		info.WritePendingEventsAfterCheckpoint = (_currentCheckpoint != null
			? _currentCheckpoint.GetWritePendingEvents()
			: 0);
		info.ReadsInProgress = /*_readDispatcher.ActiveRequestCount*/
			+ +(_closingCheckpoint != null ? _closingCheckpoint.GetReadsInProgress() : 0)
			+ (_currentCheckpoint != null ? _currentCheckpoint.GetReadsInProgress() : 0);
		info.WritesInProgress = (_closingCheckpoint != null ? _closingCheckpoint.GetWritesInProgress() : 0)
								+ (_currentCheckpoint != null ? _currentCheckpoint.GetWritesInProgress() : 0);
		info.CheckpointStatus = _inCheckpoint ? "Requested" : "";
	}

	public void StateUpdated(
		string partition, PartitionState oldState, PartitionState newState)
	{
		if (_stopped)
			return;
		EnsureStarted();
		if (_stopping)
			throw new InvalidOperationException("Stopping");

		if (partition == "" && newState.State == null) // ignore non-root partitions and non-changed states
			throw new NotSupportedException("Internal check");

		CheckStateSize(newState, partition);

		if (_usePersistentCheckpoints && partition != "")
			CapturePartitionStateUpdated(partition, oldState, newState);

		if (partition == "")
			_currentProjectionState = newState;
	}

	private void CheckStateSize(PartitionState result, string partition)
	{
		if (!_largeStateWarningLogged && result.Size >= 8_000_000 && !partition.Equals(""))
		{
			Log.Warning(
				"State size for the Projection {projectionName} for Partition {partitionName} is greater than 8 MB. State size for a projection must be less than 16 MB. Current state size for Partition {partitionName} is {stateSize} MB.",
				_namingBuilder.EffectiveProjectionName, partition, partition,
				result.Size / Math.Pow(10, 6));
			_largeStateWarningLogged = true;
		}
		else if (!_largeStateWarningLogged && result.Size >= 8_000_000 && partition.Equals(""))
		{
			Log.Warning(
				"State size for the Projection {projectionName} is greater than 8 MB. State size for a projection must be less than 16 MB. Current state size for Projection {projectionName} is {stateSize} MB.",
				_namingBuilder.EffectiveProjectionName, _namingBuilder.EffectiveProjectionName,
				result.Size / Math.Pow(10, 6));
			_largeStateWarningLogged = true;
		}
	}

	public void EventProcessed(CheckpointTag checkpointTag, float progress)
	{
		if (_stopped)
			return;
		EnsureStarted();
		if (_stopping)
			throw new InvalidOperationException("Stopping");
		_eventsProcessedAfterRestart++;
		_lastProcessedEventPosition.UpdateByCheckpointTagForward(checkpointTag);
		_lastProcessedEventProgress = progress;
		// running state only
	}

	public void EventsEmitted(EmittedEventEnvelope[] scheduledWrites, Guid causedBy, string correlationId)
	{
		if (_stopped)
			return;
		EnsureStarted();
		if (_stopping)
			throw new InvalidOperationException("Stopping");
		if (scheduledWrites != null)
		{
			foreach (var @event in scheduledWrites)
			{
				var emittedEvent = @event.Event;
				if (string.IsNullOrEmpty(@event.Event.StreamId))
				{
					Failed("Cannot write to a null stream id");
					return;
				}

				emittedEvent.SetCausedBy(causedBy);
				emittedEvent.SetCorrelationId(correlationId);
			}

			_currentCheckpoint.ValidateOrderAndEmitEvents(scheduledWrites);
		}
	}

	public bool CheckpointSuggested(CheckpointTag checkpointTag, float progress)
	{
		if (!_usePersistentCheckpoints)
			throw new InvalidOperationException("Checkpoints are not used");
		if (_stopped || _stopping)
			return true;
		EnsureStarted();
		if (checkpointTag != _lastProcessedEventPosition.LastTag) // allow checkpoint at the current position
			_lastProcessedEventPosition.UpdateByCheckpointTagForward(checkpointTag);
		_lastProcessedEventProgress = progress;
		return RequestCheckpoint(_lastProcessedEventPosition);
	}

	public void Progress(float progress)
	{
		if (_stopping || _stopped)
			return;
		_lastProcessedEventProgress = progress < -1 ? -1 : progress;
	}

	public CheckpointTag LastProcessedEventPosition
	{
		get { return _lastProcessedEventPosition.LastTag; }
	}


	public void Handle(CoreProjectionProcessingMessage.ReadyForCheckpoint message)
	{
		// ignore any messages - typically when faulted
		if (_stopped)
			return;
		// ignore any messages from previous checkpoints probably before RestartRequested
		if (message.Sender != _closingCheckpoint)
			return;
		if (!_inCheckpoint)
			throw new InvalidOperationException();
		if (_usePersistentCheckpoints)
			BeginWriteCheckpoint(_requestedCheckpointPosition, _requestedCheckpointState.Serialize());
		else
			CheckpointWritten(_requestedCheckpointPosition);
	}

	public void Handle(CoreProjectionProcessingMessage.RestartRequested message)
	{
		if (_stopped)
			return;
		RequestRestart(message.Reason);
	}

	public void Handle(CoreProjectionProcessingMessage.Failed message)
	{
		if (_stopped)
			return;
		Failed(message.Reason);
	}

	protected void PrerecordedEventsLoaded(CheckpointTag checkpointTag)
	{
		_publisher.Publish(
			new CoreProjectionProcessingMessage.PrerecordedEventsLoaded(_projectionCorrelationId, checkpointTag));
	}

	private void RequestCheckpointToStop()
	{
		EnsureStarted();
		if (!_stopping)
			throw new InvalidOperationException("Not stopping");
		if (_inCheckpoint) // checkpoint in progress.  no other writes will happen, so we can stop here.
			return;
		// do not request checkpoint if no events were processed since last checkpoint
		//NOTE: we ignore _usePersistentCheckpoints flag as we need to flush final writes before query object
		// has been disposed
		if ( /* _usePersistentCheckpoints && */
			_lastCompletedCheckpointPosition < _lastProcessedEventPosition.LastTag)
		{
			RequestCheckpoint(_lastProcessedEventPosition, forcePrepareCheckpoint: true);
			return;
		}

		_publisher.Publish(
			new CoreProjectionProcessingMessage.CheckpointCompleted(
				_projectionCorrelationId, _lastCompletedCheckpointPosition));
	}

	protected void EnsureStarted()
	{
		if (!_started)
			throw new InvalidOperationException("Not started");
	}

	/// <returns>true - if checkpoint has beem completed in-sync</returns>
	private bool RequestCheckpoint(PositionTracker lastProcessedEventPosition,
		bool forcePrepareCheckpoint = false)
	{
		if (!forcePrepareCheckpoint && !_usePersistentCheckpoints)
			throw new InvalidOperationException("Checkpoints are not allowed");
		if (_inCheckpoint)
			throw new InvalidOperationException("Checkpoint in progress");
		return StartCheckpoint(lastProcessedEventPosition, _currentProjectionState);
	}

	/// <returns>true - if checkpoint has been completed in-sync</returns>
	private bool StartCheckpoint(PositionTracker lastProcessedEventPosition, PartitionState projectionState)
	{
		Contract.Requires(_closingCheckpoint == null);
		if (projectionState == null)
			throw new ArgumentNullException("projectionState");

		CheckpointTag requestedCheckpointPosition = lastProcessedEventPosition.LastTag;
		if (requestedCheckpointPosition == _lastCompletedCheckpointPosition)
			return true; // either suggested or requested to stop

		if (_usePersistentCheckpoints) // do not emit any events if we do not use persistent checkpoints
			EmitPartitionCheckpoints();

		_inCheckpoint = true;
		_requestedCheckpointPosition = requestedCheckpointPosition;
		_requestedCheckpointState = projectionState;
		_closingCheckpoint = _currentCheckpoint;
		_currentCheckpoint = CreateProjectionCheckpoint(requestedCheckpointPosition);
		// checkpoint only after assigning new current checkpoint, as it may call back immediately
		_closingCheckpoint.Prepare(requestedCheckpointPosition);
		return false; // even if prepare completes in sync it notifies the world by a message
	}

	protected void SendPrerecordedEvent(
		EventStore.Core.Data.ResolvedEvent pair, CheckpointTag positionTag,
		long prerecordedEventMessageSequenceNumber)
	{
		var committedEvent = new ReaderSubscriptionMessage.CommittedEventDistributed(
			Guid.Empty, new ResolvedEvent(pair, null), null, -1, source: this.GetType());
		_publisher.Publish(
			EventReaderSubscriptionMessage.CommittedEventReceived.FromCommittedEventDistributed(
				committedEvent, positionTag, null, _projectionCorrelationId,
				prerecordedEventMessageSequenceNumber));
	}


	protected void RequestRestart(string reason)
	{
		_stopped = true; // ignore messages
		_publisher.Publish(new CoreProjectionProcessingMessage.RestartRequested(_projectionCorrelationId, reason));
	}

	private void Failed(string reason)
	{
		_stopped = true; // ignore messages
		_publisher.Publish(new CoreProjectionProcessingMessage.Failed(_projectionCorrelationId, reason));
	}

	protected void CheckpointWritten(CheckpointTag lastCompletedCheckpointPosition)
	{
		Contract.Requires(_closingCheckpoint != null);
		_lastCompletedCheckpointPosition = lastCompletedCheckpointPosition;
		_closingCheckpoint.Dispose();
		_closingCheckpoint = null;
		if (!_stopping)
			// ignore any writes pending in the current checkpoint (this is not the best, but they will never hit the storage, so it is safe)
			_currentCheckpoint.Start();
		_inCheckpoint = false;

		//NOTE: the next checkpoint will start by completing checkpoint work item
		_publisher.Publish(
			new CoreProjectionProcessingMessage.CheckpointCompleted(
				_projectionCorrelationId, _lastCompletedCheckpointPosition));
	}

	public virtual void BeginLoadPrerecordedEvents(CheckpointTag checkpointTag)
	{
		PrerecordedEventsLoaded(checkpointTag);
	}
}
