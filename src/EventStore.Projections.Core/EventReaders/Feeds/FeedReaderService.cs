using EventStore.Core.Bus;
using EventStore.Core.Services.TimerService;
using EventStore.Projections.Core.Messages.EventReaders.Feeds;
using EventStore.Projections.Core.Services;

namespace EventStore.Projections.Core.EventReaders.Feeds;

public class FeedReaderService : IHandle<FeedReaderMessage.ReadPage>
{
	private readonly ReaderSubscriptionDispatcher _subscriptionDispatcher;

	private readonly ITimeProvider _timeProvider;

	public FeedReaderService(ReaderSubscriptionDispatcher subscriptionDispatcher, ITimeProvider timeProvider)
	{
		_subscriptionDispatcher = subscriptionDispatcher;
		_timeProvider = timeProvider;
	}

	public void Handle(FeedReaderMessage.ReadPage message)
	{
		var reader = FeedReader.Create(_subscriptionDispatcher, message, _timeProvider);
		reader.Start();
	}
}
