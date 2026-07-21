using System;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Messages;
using EventStore.Core.Metrics;
using EventStore.Core.Services.PersistentSubscription;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class PersistentSubscriptionMetricsTests
{
	readonly PersistentSubscriptionTracker _sut = new();

	public PersistentSubscriptionMetricsTests()
	{
		var statsSampleOne = new MonitoringMessage.PersistentSubscriptionInfo()
		{
			EventSource = "test",
			GroupName = "testGroup",
			AveragePerSecond = 1,
			BufferSize = 500,
			CheckPointAfterMilliseconds = 1000,
			Connections = Enumerable.Range(0, 1001).Select(x => new MonitoringMessage.ConnectionInfo()).ToList(),
			CountSinceLastMeasurement = 5,
			ExtraStatistics = false,
			LastCheckpointedEventPosition = "1013",
			LastKnownEventPosition = "1011",
			LiveBufferCount = 0,
			LiveBufferSize = 500,
			MaxCheckPointCount = 500,
			MaxRetryCount = 5,
			MaxSubscriberCount = 10,
			MessageTimeoutMilliseconds = 1000,
			MinCheckPointCount = 10,
			NamedConsumerStrategy = "Round Robin",
			OldestParkedMessage = 1007,
			OutstandingMessagesCount = 2,
			ParkedDueToClientNak = 1015,
			ParkedDueToMaxRetries = 1016,
			ParkedMessageReplays = 1017,
			ParkedMessageTruncates = 1021,
			ParkedMessageCount = 1003,
			ReadBatchSize = 20,
			ReadBufferCount = 0,
			ResolveLinktos = true,
			RetryBufferCount = 0,
			StartFrom = 0.ToString(),
			Status = PersistentSubscriptionState.Live.ToString(),
			TotalInFlightMessages = 1005,
			TotalItems = 1009,
		};

		var statsSampleTwo = new MonitoringMessage.PersistentSubscriptionInfo()
		{
			EventSource = "$all",
			GroupName = "testGroup",
			AveragePerSecond = 1,
			BufferSize = 500,
			CheckPointAfterMilliseconds = 1000,
			Connections = Enumerable.Range(0, 1002).Select(x => new MonitoringMessage.ConnectionInfo()).ToList(),
			CountSinceLastMeasurement = 5,
			ExtraStatistics = false,
			LastCheckpointedEventPosition = "C:1014/P:5",
			LastKnownEventPosition = "C:1012/P:10",
			LiveBufferCount = 0,
			LiveBufferSize = 500,
			MaxCheckPointCount = 500,
			MaxRetryCount = 5,
			MaxSubscriberCount = 10,
			MessageTimeoutMilliseconds = 1000,
			MinCheckPointCount = 10,
			NamedConsumerStrategy = "Round Robin",
			OldestParkedMessage = 1008,
			OutstandingMessagesCount = 2,
			ParkedDueToClientNak = 1018,
			ParkedDueToMaxRetries = 1019,
			ParkedMessageReplays = 1020,
			ParkedMessageTruncates = 1022,
			ParkedMessageCount = 1004,
			ReadBatchSize = 20,
			ReadBufferCount = 0,
			ResolveLinktos = true,
			RetryBufferCount = 0,
			StartFrom = 0.ToString(),
			Status = PersistentSubscriptionState.Live.ToString(),
			TotalInFlightMessages = 1006,
			TotalItems = 1010,
		};

		_sut.OnNewStats([
			statsSampleOne,
			statsSampleTwo,
		]);
	}

	[Fact]
	public void ObserveConnectionsCount()
	{
		var measurements = _sut.ObserveConnectionsCount();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1001),
			AssertMeasurement("$all", "testGroup", 1002));
	}

	[Fact]
	public void ObserveParkedMessages()
	{
		var measurements = _sut.ObserveParkedMessages();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1003),
			AssertMeasurement("$all", "testGroup", 1004));
	}

	[Fact]
	public void ObserveParkMessageRequests()
	{
		var measurements = _sut.ObserveParkMessageRequests();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", "client_nack", 1015),
			AssertMeasurement("test", "testGroup", "max_retries", 1016),
			AssertMeasurement("$all", "testGroup", "client_nack", 1018),
			AssertMeasurement("$all", "testGroup", "max_retries", 1019));
	}

	[Fact]
	public void ObserveParkedMessageReplays()
	{
		var measurements = _sut.ObserveParkedMessageReplays();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1017),
			AssertMeasurement("$all", "testGroup", 1020));
	}

	[Fact]
	public void ObserveParkedMessageTruncates()
	{
		var measurements = _sut.ObserveParkedMessageTruncates();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1021),
			AssertMeasurement("$all", "testGroup", 1022));
	}

	[Fact]
	public void ObserveInFlightMessages()
	{
		var measurements = _sut.ObserveInFlightMessages();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1005),
			AssertMeasurement("$all", "testGroup", 1006));
	}

	[Fact]
	public void ObserveOldestParkedMessage()
	{
		var measurements = _sut.ObserveOldestParkedMessage();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1007),
			AssertMeasurement("$all", "testGroup", 1008));
	}

	[Fact]
	public void ObserveItemsProcessed()
	{
		var measurements = _sut.ObserveItemsProcessed();
		Assert.Collection(measurements,
			AssertMeasurement("test", "testGroup", 1009),
			AssertMeasurement("$all", "testGroup", 1010));
	}

	[Fact]
	public void ObserveLastKnownEvent()
	{
		var measurements = _sut.ObserveLastKnownEvent();
		AssertMeasurement("test", "testGroup", 1011)(Assert.Single(measurements));
	}

	[Fact]
	public void ObserveLastKnownEventCommitPosition()
	{
		var measurements = _sut.ObserveLastKnownEventCommitPosition();
		AssertMeasurement("$all", "testGroup", 1012)(Assert.Single(measurements));
	}

	[Fact]
	public void ObserveLastCheckpointedEvent()
	{
		var measurements = _sut.ObserveLastCheckpointedEvent();
		AssertMeasurement("test", "testGroup", 1013)(Assert.Single(measurements));
	}

	[Fact]
	public void ObserveLastCheckpointedEventCommitPosition()
	{
		var measurements = _sut.ObserveLastCheckpointedEventCommitPosition();
		AssertMeasurement("$all", "testGroup", 1014)(Assert.Single(measurements));
	}

	static Action<Measurement<long>> AssertMeasurement(
		string sourceName,
		string groupName,
		long expectedValue) =>

		actualMeasurement =>
		{
			Assert.Equal(expectedValue, actualMeasurement.Value);
			Assert.Collection(
				actualMeasurement.Tags.ToArray(),
				tag =>
				{
					Assert.Equal(TrogonAttributeNames.PersistentSubscriptionStream, tag.Key);
					Assert.Equal(sourceName, tag.Value);
				},
				tag =>
				{
					Assert.Equal(TrogonAttributeNames.PersistentSubscriptionGroup, tag.Key);
					Assert.Equal(groupName, tag.Value);
				}
			);
		};

	static Action<Measurement<long>> AssertMeasurement(
		string sourceName,
		string groupName,
		string reason,
		long expectedValue) =>

		actualMeasurement =>
		{
			Assert.Equal(expectedValue, actualMeasurement.Value);
			Assert.Collection(
				actualMeasurement.Tags.ToArray(),
				tag =>
				{
					Assert.Equal(TrogonAttributeNames.PersistentSubscriptionStream, tag.Key);
					Assert.Equal(sourceName, tag.Value);
				},
				tag =>
				{
					Assert.Equal(TrogonAttributeNames.PersistentSubscriptionGroup, tag.Key);
					Assert.Equal(groupName, tag.Value);
				},
				tag =>
				{
					Assert.Equal(TrogonAttributeNames.PersistentSubscriptionReason, tag.Key);
					Assert.Equal(reason, tag.Value);
				}
			);
		};
}
