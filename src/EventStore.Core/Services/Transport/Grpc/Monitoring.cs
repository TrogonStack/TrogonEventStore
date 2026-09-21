using System;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc
{
	internal partial class Monitoring : EventStore.Client.Monitoring.Monitoring.MonitoringBase
	{
		private readonly IPublisher _publisher;
		private readonly IConnectionStatsProvider _connectionStatsProvider;

		public override Task Stats(StatsReq request, IServerStreamWriter<StatsResp> responseStream, ServerCallContext context)
		{
			var useGrouping = request.HasUseGrouping ? request.UseGrouping : false;
			if (!useGrouping && !string.IsNullOrEmpty(request.StatsPath))
			{
				throw new RpcException(
					new Status(StatusCode.InvalidArgument,
						"Dynamic stats selection works only with grouping enabled"));
			}

			return StreamStats();

			async Task StreamStats()
			{
				var delay = TimeSpan.FromMilliseconds(request.RefreshTimePeriodInMs);
				while (!context.CancellationToken.IsCancellationRequested)
				{
					var completed = await GetFreshStats(context.CancellationToken);
					if (!completed.Success)
					{
						throw new RpcException(
							new Status(StatusCode.NotFound, "Statistics path was not found."));
					}

					var response = new StatsResp
					{
						StructuredStats = MonitoringStats.ToValue(completed.Stats)
					};
					if (!useGrouping)
					{
						foreach (var (key, value) in completed.Stats)
						{
							if (value is not null)
							{
								response.Stats.Add(key, value.ToString());
							}
						}
					}

					await responseStream.WriteAsync(response);
					await Task.Delay(delay, context.CancellationToken);
				}
			}

			Task<MonitoringMessage.GetFreshStatsCompleted> GetFreshStats(System.Threading.CancellationToken cancellationToken)
			{
				var responseSource =
					new TaskCompletionSource<MonitoringMessage.GetFreshStatsCompleted>(
						TaskCreationOptions.RunContinuationsAsynchronously);
				var envelope = new CallbackEnvelope(message =>
				{
					if (message is not MonitoringMessage.GetFreshStatsCompleted completed)
					{
						responseSource.TrySetException(
							UnknownMessage<MonitoringMessage.GetFreshStatsCompleted>(message));
						return;
					}

					responseSource.TrySetResult(completed);
				});

				_publisher.Publish(
					new MonitoringMessage.GetFreshStats(
						envelope,
						MonitoringStats.GetStatSelector(request.StatsPath),
						request.UseMetadata,
						useGrouping));

				return responseSource.Task.WaitAsync(cancellationToken);
			}
		}

		public override Task<ConnectionStatsResp> ConnectionStats(
			ConnectionStatsReq request,
			ServerCallContext context)
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			var response = new ConnectionStatsResp();
			foreach (var connection in _connectionStatsProvider.Snapshot())
			{
				response.Connections.Add(new EventStore.Client.Monitoring.ConnectionStats
				{
					RemoteEndpoint = connection.RemoteEndPoint ?? string.Empty,
					LocalEndpoint = connection.LocalEndPoint ?? string.Empty,
					ClientConnectionName = connection.ClientName ?? string.Empty,
					ConnectionId = connection.ConnectionId ?? string.Empty,
					TotalBytesSent = connection.TotalBytesSent,
					TotalBytesReceived = connection.TotalBytesReceived,
					PendingSendBytes = connection.PendingSendBytes,
					PendingReceivedBytes = connection.PendingReceivedBytes,
					IsTls = connection.IsTls,
					Protocol = connection.Protocol ?? string.Empty,
					Application = connection.Application ?? string.Empty,
					ConnectedAt = Timestamp.FromDateTimeOffset(connection.ConnectedAt)
				});
			}

			return Task.FromResult(response);
		}

		public override Task<ReplicationStatsResp> ReplicationStats(ReplicationStatsReq request, ServerCallContext context)
		{
			var responseSource =
				new TaskCompletionSource<ReplicationStatsResp>(TaskCreationOptions.RunContinuationsAsynchronously);
			var envelope = new CallbackEnvelope(message =>
			{
				if (message is not ReplicationMessage.GetReplicationStatsCompleted completed)
				{
					responseSource.TrySetException(
						UnknownMessage<ReplicationMessage.GetReplicationStatsCompleted>(message));
					return;
				}

				var response = new ReplicationStatsResp();
				foreach (var stats in completed.ReplicationStats)
				{
					response.Stats.Add(new ReplicationStats
					{
						SubscriptionId = stats.SubscriptionId.ToString("D"),
						ConnectionId = stats.ConnectionId.ToString("D"),
						SubscriptionEndpoint = stats.SubscriptionEndpoint ?? string.Empty,
						TotalBytesSent = stats.TotalBytesSent,
						TotalBytesReceived = stats.TotalBytesReceived,
						PendingSendBytes = stats.PendingSendBytes,
						PendingReceivedBytes = stats.PendingReceivedBytes,
						SendQueueSize = stats.SendQueueSize
					});
				}

				responseSource.TrySetResult(response);
			});

			_publisher.Publish(new ReplicationMessage.GetReplicationStats(envelope));
			return responseSource.Task.WaitAsync(context.CancellationToken);
		}

		public Monitoring(IPublisher publisher) : this(publisher, null)
		{
		}

		public Monitoring(IPublisher publisher, IConnectionStatsProvider connectionStatsProvider)
		{
			_publisher = publisher;
			_connectionStatsProvider = connectionStatsProvider ?? EmptyConnectionStatsProvider.Instance;
		}

		private static Exception UnknownMessage<T>(Message message) where T : Message =>
			new RpcException(
				new Status(StatusCode.Unknown,
					$"Envelope callback expected {typeof(T).Name}, received {message.GetType().Name} instead"));
	}
}
