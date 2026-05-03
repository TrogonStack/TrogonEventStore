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

	public static T ForV2<T>(object value)
	{
		if (typeof(TLogFormat) == typeof(LogFormat.V2))
		{
			if (typeof(TStreamId) != typeof(string))
				throw new InvalidOperationException();
			return (T)value;
		}
		throw new InvalidOperationException();
	}

	public static ILogFormatAbstractorFactory<TStreamId> LogFormatFactory { get; } =
		ForV2<ILogFormatAbstractorFactory<TStreamId>>(new LogV2FormatAbstractorFactory());

	public static IRecordFactory<TStreamId> RecordFactory { get; } =
		ForV2<IRecordFactory<TStreamId>>(new LogV2RecordFactory());

	public static bool SupportsExplicitTransactions { get; } = true;

	public static TStreamId EmptyStreamId { get; } = ForV2<TStreamId>(string.Empty);

	public static TStreamId StreamId { get; } = ForV2<TStreamId>("stream");
	public static TStreamId StreamId2 { get; } = ForV2<TStreamId>("stream2");

	public static TStreamId EventTypeId { get; } = ForV2<TStreamId>("eventType");
	public static TStreamId EventTypeId2 { get; } = ForV2<TStreamId>("eventType2");
	public static TStreamId EmptyEventTypeId { get; } = ForV2<TStreamId>(string.Empty);

	public static void CheckIfExplicitTransactionsSupported()
	{
	}

	public static void EnsureV0PrepareSupported()
	{
	}
}
