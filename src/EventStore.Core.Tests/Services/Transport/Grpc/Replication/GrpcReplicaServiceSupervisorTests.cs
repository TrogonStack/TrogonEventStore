using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class GrpcReplicaServiceSupervisorTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[TestCase(false)]
	[TestCase(true)]
	public async Task pre_replica_state_starts_and_tracks_a_stream_for_the_advertised_http_endpoints(
		bool readOnlyReplica)
	{
		var fixture = CreateFixture();
		var correlationId = Guid.NewGuid();
		var connectionCorrelationId = Guid.NewGuid();
		SystemMessage.StateChangeMessage state = readOnlyReplica
			? new SystemMessage.BecomePreReadOnlyReplica(correlationId, connectionCorrelationId, fixture.Leader)
			: new SystemMessage.BecomePreReplica(correlationId, connectionCorrelationId, fixture.Leader);

		await fixture.Supervisor.HandleAsync(state, CancellationToken.None);

		var request = fixture.Factory.Requests.Single();
		Assert.Multiple(() =>
		{
			Assert.That(request.Endpoints.LeaderEndPoint, Is.EqualTo(fixture.Leader.HttpEndPoint));
			Assert.That(request.Endpoints.AdvertisedReplicaEndPoint, Is.EqualTo(fixture.AdvertisedEndPoint));
			Assert.That(request.Service.StartCalls, Is.EqualTo(1));
			Assert.That(fixture.TrackedTasks.Single(), Is.SameAs(request.Service.Task));
		});
	}

	[TestCase(VNodeState.CatchingUp)]
	[TestCase(VNodeState.Clone)]
	[TestCase(VNodeState.Follower)]
	[TestCase(VNodeState.ReadOnlyReplica)]
	public async Task replica_states_keep_the_active_stream(VNodeState state)
	{
		var fixture = CreateFixture();
		var correlationId = Guid.NewGuid();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(correlationId, Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var service = fixture.Factory.Requests.Single().Service;

		await fixture.Supervisor.HandleAsync(
			CreateReplicaState(state, correlationId, fixture.Leader),
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(service.StopCalls, Is.Zero);
			Assert.That(fixture.Factory.Requests, Has.Count.EqualTo(1));
		});
	}

	[TestCase(VNodeState.Leader)]
	[TestCase(VNodeState.ShuttingDown)]
	public async Task departure_and_shutdown_await_the_active_stream(VNodeState state)
	{
		var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fixture = CreateFixture(stopGate);
		var correlationId = Guid.NewGuid();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(correlationId, Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var service = fixture.Factory.Requests.Single().Service;

		var departure = fixture.Supervisor.HandleAsync(
			CreateDepartureState(state),
			CancellationToken.None).AsTask();
		await service.StopStarted.Task.WaitAsync(Timeout);
		Assert.That(departure.IsCompleted, Is.False);

		stopGate.SetResult();
		await departure.WaitAsync(Timeout);
		Assert.That(service.StopCalls, Is.EqualTo(1));
	}

	[Test]
	public async Task reconnect_stops_the_previous_stream_before_starting_its_replacement()
	{
		var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fixture = CreateFixture(stopGate);
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var previous = fixture.Factory.Requests.Single().Service;

		var reconnect = fixture.Supervisor.HandleAsync(
			new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), fixture.Leader),
			CancellationToken.None).AsTask();
		await previous.StopStarted.Task.WaitAsync(Timeout);
		Assert.That(fixture.Factory.Requests, Has.Count.EqualTo(1));

		stopGate.SetResult();
		await reconnect.WaitAsync(Timeout);
		Assert.Multiple(() =>
		{
			Assert.That(previous.StopCalls, Is.EqualTo(1));
			Assert.That(fixture.Factory.Requests, Has.Count.EqualTo(2));
			Assert.That(fixture.TrackedTasks, Has.Count.EqualTo(2));
		});
	}

	[Test]
	public async Task stale_stream_publications_are_fenced_after_replacement()
	{
		var fixture = CreateFixture();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var previous = fixture.Factory.Requests.Single().Service;

		await fixture.Supervisor.HandleAsync(
			new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var current = fixture.Factory.Requests.Last().Service;
		var previousLoss = new SystemMessage.VNodeConnectionLost(fixture.Leader.HttpEndPoint, Guid.NewGuid());
		var currentLoss = new SystemMessage.VNodeConnectionLost(fixture.Leader.HttpEndPoint, Guid.NewGuid());

		previous.Publish(previousLoss);
		current.Publish(currentLoss);

		Assert.That(fixture.Publisher.Messages, Is.EqualTo(new Message[] { currentLoss }));
	}

	[Test]
	public async Task replacement_waits_for_an_in_progress_publication()
	{
		var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var publishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fixture = CreateFixture(beforePublish: _ =>
		{
			publishStarted.TrySetResult();
			publishGate.Task.GetAwaiter().GetResult();
		});
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var previous = fixture.Factory.Requests.Single().Service;
		var publication = Task.Run(() => previous.Publish(
			new SystemMessage.VNodeConnectionLost(fixture.Leader.HttpEndPoint, Guid.NewGuid())));
		await publishStarted.Task.WaitAsync(Timeout);

		var reconnect = Task.Run(async () =>
		{
			reconnectStarted.TrySetResult();
			await fixture.Supervisor.HandleAsync(
				new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), fixture.Leader),
				CancellationToken.None);
		});
		await reconnectStarted.Task.WaitAsync(Timeout);
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(100));
			Assert.That(reconnect.IsCompleted, Is.False);
		}
		finally
		{
			publishGate.TrySetResult();
		}

		await Task.WhenAll(publication, reconnect).WaitAsync(Timeout);
	}

	[Test]
	public async Task publication_fence_allows_synchronous_publisher_reentrancy()
	{
		var nested = new SystemMessage.VNodeConnectionLost(
			new DnsEndPoint("nested-leader.internal", 2113), Guid.NewGuid());
		FakeGrpcReplicaService service = null;
		var reentered = false;
		var fixture = CreateFixture(beforePublish: _ =>
		{
			if (reentered)
			{
				return;
			}

			reentered = true;
			service.Publish(nested);
		});
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		service = fixture.Factory.Requests.Single().Service;
		var outer = new SystemMessage.VNodeConnectionLost(fixture.Leader.HttpEndPoint, Guid.NewGuid());

		service.Publish(outer);

		Assert.That(fixture.Publisher.Messages, Is.EqualTo(new Message[] { nested, outer }));
	}

	[Test]
	public async Task starting_failure_publishes_leader_connection_failed()
	{
		var failure = new InvalidOperationException("start failed");
		var fixture = CreateFixture(startException: failure);
		var connectionCorrelationId = Guid.NewGuid();

		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), connectionCorrelationId, fixture.Leader),
			CancellationToken.None);

		var failed = fixture.Publisher.Messages.OfType<ReplicationMessage.LeaderConnectionFailed>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(failed.LeaderConnectionCorrelationId, Is.EqualTo(connectionCorrelationId));
			Assert.That(failed.Leader, Is.SameAs(fixture.Leader));
			Assert.That(fixture.TrackedTasks, Is.Empty);
		});
	}

	[Test]
	public async Task client_creation_failure_publishes_leader_connection_failed()
	{
		var failure = new InvalidOperationException("client creation failed");
		var fixture = CreateFixture(createException: failure);
		var connectionCorrelationId = Guid.NewGuid();

		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), connectionCorrelationId, fixture.Leader),
			CancellationToken.None);

		var failed = fixture.Publisher.Messages.OfType<ReplicationMessage.LeaderConnectionFailed>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(failed.LeaderConnectionCorrelationId, Is.EqualTo(connectionCorrelationId));
			Assert.That(failed.Leader, Is.SameAs(fixture.Leader));
			Assert.That(fixture.Factory.Requests, Is.Empty);
			Assert.That(fixture.TrackedTasks, Is.Empty);
		});
	}

	[Test]
	public async Task a_subscription_retry_uses_a_fresh_stream()
	{
		var fixture = CreateFixture();
		var stateCorrelationId = Guid.NewGuid();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(stateCorrelationId, Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var first = fixture.Factory.Requests.Single().Service;
		var firstSubscription = new ReplicationMessage.SubscribeToLeader(
			stateCorrelationId, fixture.Leader.InstanceId, Guid.NewGuid());

		await fixture.Supervisor.HandleAsync(firstSubscription, CancellationToken.None);
		var retry = new ReplicationMessage.SubscribeToLeader(
			stateCorrelationId, fixture.Leader.InstanceId, Guid.NewGuid());
		await fixture.Supervisor.HandleAsync(retry, CancellationToken.None);

		var replacement = fixture.Factory.Requests.Last().Service;
		Assert.Multiple(() =>
		{
			Assert.That(first.Subscriptions, Is.EqualTo(new[] { firstSubscription }));
			Assert.That(first.StopCalls, Is.EqualTo(1));
			Assert.That(replacement.Subscriptions, Is.EqualTo(new[] { retry }));
			Assert.That(fixture.Factory.Requests, Has.Count.EqualTo(2));
		});
	}

	[Test]
	public async Task a_completed_stream_is_replaced_before_subscribing()
	{
		var fixture = CreateFixture();
		var stateCorrelationId = Guid.NewGuid();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(stateCorrelationId, Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var completed = fixture.Factory.Requests.Single().Service;
		completed.Complete();
		var subscription = new ReplicationMessage.SubscribeToLeader(
			stateCorrelationId, fixture.Leader.InstanceId, Guid.NewGuid());

		await fixture.Supervisor.HandleAsync(subscription, CancellationToken.None);

		var replacement = fixture.Factory.Requests.Last().Service;
		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Requests, Has.Count.EqualTo(2));
			Assert.That(completed.Subscriptions, Is.Empty);
			Assert.That(replacement.Subscriptions, Is.EqualTo(new[] { subscription }));
		});
	}

	[Test]
	public async Task acknowledgements_are_routed_only_to_the_active_stream()
	{
		var fixture = CreateFixture();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var active = fixture.Factory.Requests.Single().Service;
		var acknowledgement = new ReplicationMessage.AckLogPosition(Guid.NewGuid(), 200, 180);

		fixture.Supervisor.Handle(acknowledgement);

		Assert.That(active.Acknowledgements, Is.EqualTo(new[] { acknowledgement }));
	}

	[Test]
	public async Task acknowledgement_during_stream_creation_does_not_fail_the_replacement()
	{
		GrpcReplicaServiceSupervisor supervisor = null;
		var acknowledgement = new ReplicationMessage.AckLogPosition(Guid.NewGuid(), 200, 180);
		var fixture = CreateFixture(beforeCreateReturns: _ => supervisor.Handle(acknowledgement));
		supervisor = fixture.Supervisor;

		await supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);

		Assert.That(fixture.Factory.Requests, Has.Count.EqualTo(1));
		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Requests.Single().Service.StartCalls, Is.EqualTo(1));
			Assert.That(fixture.Factory.Requests.Single().Service.Acknowledgements, Is.Empty);
			Assert.That(fixture.Publisher.Messages, Has.None.TypeOf<ReplicationMessage.LeaderConnectionFailed>());
		});
	}

	[Test]
	public async Task replacement_waits_for_an_in_progress_acknowledgement()
	{
		var acknowledgementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var acknowledgementGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fixture = CreateFixture();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), fixture.Leader),
			CancellationToken.None);
		var previous = fixture.Factory.Requests.Single().Service;
		previous.BeforeAcknowledgement = _ =>
		{
			acknowledgementStarted.TrySetResult();
			acknowledgementGate.Task.GetAwaiter().GetResult();
		};
		var acknowledgement = new ReplicationMessage.AckLogPosition(Guid.NewGuid(), 200, 180);
		var acknowledgementTask = Task.Run(() => fixture.Supervisor.Handle(acknowledgement));
		await acknowledgementStarted.Task.WaitAsync(Timeout);

		var reconnect = Task.Run(async () =>
		{
			reconnectStarted.TrySetResult();
			await fixture.Supervisor.HandleAsync(
				new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), fixture.Leader),
				CancellationToken.None);
		});
		await reconnectStarted.Task.WaitAsync(Timeout);
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(100));
			Assert.Multiple(() =>
			{
				Assert.That(previous.StopCalls, Is.Zero);
				Assert.That(reconnect.IsCompleted, Is.False);
			});
		}
		finally
		{
			acknowledgementGate.TrySetResult();
		}

		await Task.WhenAll(acknowledgementTask, reconnect).WaitAsync(Timeout);
		Assert.Multiple(() =>
		{
			Assert.That(previous.Acknowledgements, Is.EqualTo(new[] { acknowledgement }));
			Assert.That(previous.StopCalls, Is.EqualTo(1));
		});
	}

	[Test]
	public async Task final_shutdown_state_is_ignored_after_disposal()
	{
		var fixture = CreateFixture();

		await fixture.Supervisor.DisposeAsync();
		await fixture.Supervisor.DisposeAsync();
		await fixture.Supervisor.HandleAsync(
			new SystemMessage.BecomeShutdown(Guid.NewGuid()),
			CancellationToken.None);
	}

	private static Fixture CreateFixture(
		TaskCompletionSource stopGate = null,
		Exception startException = null,
		Exception createException = null,
		Action<Message> beforePublish = null,
		Action<FakeGrpcReplicaService> beforeCreateReturns = null)
	{
		var publisher = new ConcurrentPublisher(beforePublish);
		var factory = new FakeGrpcReplicaServiceFactory(
			stopGate,
			startException,
			createException,
			beforeCreateReturns);
		var advertisedEndPoint = new DnsEndPoint("replica.internal", 2113);
		var trackedTasks = new List<Task>();
		var supervisor = new GrpcReplicaServiceSupervisor(
			publisher,
			factory,
			advertisedEndPoint,
			trackedTasks.Add);

		return new Fixture(
			supervisor,
			factory,
			publisher,
			trackedTasks,
			CreateLeader(),
			advertisedEndPoint);
	}

	private static MemberInfo CreateLeader() => MemberInfo.ForVNode(
		Guid.NewGuid(),
		DateTime.UtcNow,
		VNodeState.Leader,
		true,
		new DnsEndPoint("leader-replication.internal", 1112),
		null,
		null,
		null,
		new DnsEndPoint("leader.internal", 2113),
		null,
		0,
		0,
		0,
		0,
		0,
		0,
		0,
		Guid.NewGuid(),
		0,
		false);

	private static SystemMessage.StateChangeMessage CreateReplicaState(
		VNodeState state,
		Guid correlationId,
		MemberInfo leader) => state switch
		{
			VNodeState.CatchingUp => new SystemMessage.BecomeCatchingUp(correlationId, leader),
			VNodeState.Clone => new SystemMessage.BecomeClone(correlationId, leader),
			VNodeState.Follower => new SystemMessage.BecomeFollower(correlationId, leader),
			VNodeState.ReadOnlyReplica => new SystemMessage.BecomeReadOnlyReplica(correlationId, leader),
			_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
		};

	private static SystemMessage.StateChangeMessage CreateDepartureState(VNodeState state) => state switch
	{
		VNodeState.Leader => new SystemMessage.BecomeLeader(Guid.NewGuid()),
		VNodeState.ShuttingDown => new SystemMessage.BecomeShuttingDown(
			Guid.NewGuid(), exitProcess: false, shutdownHttp: true),
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
	};

	private sealed record Fixture(
		GrpcReplicaServiceSupervisor Supervisor,
		FakeGrpcReplicaServiceFactory Factory,
		ConcurrentPublisher Publisher,
		List<Task> TrackedTasks,
		MemberInfo Leader,
		EndPoint AdvertisedEndPoint);

	private sealed class ConcurrentPublisher(Action<Message> beforePublish) : IPublisher
	{
		public ConcurrentQueue<Message> Messages { get; } = new();

		public void Publish(Message message)
		{
			beforePublish?.Invoke(message);
			Messages.Enqueue(message);
		}
	}

	private sealed class FakeGrpcReplicaServiceFactory(
		TaskCompletionSource stopGate,
		Exception startException,
		Exception createException,
		Action<FakeGrpcReplicaService> beforeCreateReturns) : IGrpcReplicaServiceFactory
	{
		public List<FactoryRequest> Requests { get; } = new();

		public IGrpcReplicaService Create(IPublisher publisher, GrpcReplicaConnectionEndpoints endpoints)
		{
			if (createException is not null)
			{
				throw createException;
			}

			var service = new FakeGrpcReplicaService(publisher, stopGate, startException);
			beforeCreateReturns?.Invoke(service);
			Requests.Add(new FactoryRequest(endpoints, service));
			return service;
		}
	}

	private sealed record FactoryRequest(
		GrpcReplicaConnectionEndpoints Endpoints,
		FakeGrpcReplicaService Service);

	private sealed class FakeGrpcReplicaService(
		IPublisher publisher,
		TaskCompletionSource stopGate,
		Exception startException) : IGrpcReplicaService
	{
		private readonly TaskCompletionSource _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public int StartCalls { get; private set; }
		public int StopCalls { get; private set; }
		public TaskCompletionSource StopStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public List<ReplicationMessage.SubscribeToLeader> Subscriptions { get; } = new();
		public List<ReplicationMessage.AckLogPosition> Acknowledgements { get; } = new();
		public Action<ReplicationMessage.AckLogPosition> BeforeAcknowledgement { private get; set; }
		public Task Task => _completion.Task;

		public Task Start()
		{
			StartCalls++;
			if (startException is not null)
			{
				throw startException;
			}

			return Task;
		}

		public ValueTask HandleAsync(
			ReplicationMessage.SubscribeToLeader message,
			CancellationToken cancellationToken)
		{
			Subscriptions.Add(message);
			return ValueTask.CompletedTask;
		}

		public void Handle(ReplicationMessage.AckLogPosition message)
		{
			BeforeAcknowledgement?.Invoke(message);
			Acknowledgements.Add(message);
		}

		public void Complete() => _completion.TrySetResult();

		public async ValueTask StopAsync()
		{
			StopCalls++;
			StopStarted.TrySetResult();
			if (stopGate is not null)
			{
				await stopGate.Task;
			}

			_completion.TrySetResult();
		}

		public void Publish(Message message) => publisher.Publish(message);
	}
}
