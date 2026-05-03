using System;
using EventStore.Core.LogAbstraction;
using EventStore.Core.LogV2;

namespace EventStore.Core.Tests;

public class LogFormat
{
	public class V2 { }
}

public static class LogFormatHelper<TLogFormat, TStreamId>
{
	public static bool IsV2 => typeof(TLogFormat) == typeof(LogFormat.V2);

	public static T Choose<T>(object v2, object v3)
	{
		if (typeof(TLogFormat) == typeof(LogFormat.V2))
		{
			if (typeof(TStreamId) != typeof(string))
				throw new InvalidOperationException();
			return (T)v2;
		}
		throw new InvalidOperationException();
	}

	public static ILogFormatAbstractorFactory<TStreamId> LogFormatFactory { get; } =
		Choose<ILogFormatAbstractorFactory<TStreamId>>(new LogV2FormatAbstractorFactory(), null);

	public static IRecordFactory<TStreamId> RecordFactory { get; } =
		Choose<IRecordFactory<TStreamId>>(new LogV2RecordFactory(), null);

	public static bool SupportsExplicitTransactions { get; } = true;

	public static TStreamId EmptyStreamId { get; } = Choose<TStreamId>(string.Empty, null);

	public static TStreamId StreamId { get; } = Choose<TStreamId>("stream", null);
	public static TStreamId StreamId2 { get; } = Choose<TStreamId>("stream2", null);

	public static TStreamId EventTypeId { get; } = Choose<TStreamId>("eventType", null);
	public static TStreamId EventTypeId2 { get; } = Choose<TStreamId>("eventType2", null);
	public static TStreamId EmptyEventTypeId { get; } = Choose<TStreamId>(string.Empty, null);

	public static void CheckIfExplicitTransactionsSupported()
	{
	}

	public static void EnsureV0PrepareSupported()
	{
	}
}
