using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Tests;
using EventStore.Core.TransactionLog.Chunks;
using NUnit.Framework.Internal;
using NUnit.Framework;

namespace EventStore.Core.Tests.Integration;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_a_single_node_is_restarted_multiple_times<TLogFormat, TStreamId> : specification_with_a_single_node<TLogFormat, TStreamId>
{
	private List<Guid> _epochIds = new List<Guid>();
	private const int _numberOfNodeStarts = 5;
	private static readonly TimeSpan RestartTimeout = TimeSpan.FromMinutes(3);
	private LogFormatAbstractor<TStreamId> _logFormat;

	protected override TimeSpan Timeout { get; } =
		TimeSpan.FromSeconds((RestartTimeout.TotalSeconds * _numberOfNodeStarts) + 120);

	protected override async Task Given()
	{
		Guid? previousEpochId = null;
		var epochId = await GetLastEpochId(previousEpochId);
		_epochIds.Add(epochId);
		previousEpochId = epochId;

		for (int i = 0; i < _numberOfNodeStarts - 1; i++)
		{
			await ShutdownNode();
			await StartNode();
			epochId = await GetLastEpochId(previousEpochId);
			_epochIds.Add(epochId);
			previousEpochId = epochId;
		}

		await base.Given();
	}

	private async Task<Guid> GetLastEpochId(Guid? previousEpochId)
	{
		_logFormat ??= LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory.Create(new() {
			IndexDirectory = GetFilePathFor("epoch-index"),
		});

		await _node.WaitForTcpEndPoint().WaitAsync(RestartTimeout);
		var wait = Stopwatch.StartNew();
		while (wait.Elapsed < RestartTimeout)
		{
			var epochId = await TryGetLastEpochId();
			if (epochId is { } currentEpochId && currentEpochId != previousEpochId)
				return currentEpochId;

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		throw new TimeoutException(
			$"Expected startup to persist a new epoch before restart assertions. Previous epoch: {previousEpochId}");
	}

	private async Task<Guid?> TryGetLastEpochId()
	{
		var bus = new SynchronousScheduler(nameof(when_a_single_node_is_restarted_multiple_times<TLogFormat, TStreamId>));
		await using var writer = new TFChunkWriter(_node.Db);
		writer.Open();

		var epochManager = new EpochManager<TStreamId>(
			bus,
			10,
			_node.Db.Config.EpochCheckpoint,
			writer,
			initialReaderCount: 1,
			maxReaderCount: 5,
			readerFactory: () => new TFChunkReader(_node.Db, _node.Db.Config.WriterCheckpoint),
			_logFormat.RecordFactory,
			_logFormat.StreamNameIndex,
			_logFormat.EventTypeIndex,
			_logFormat.CreatePartitionManager(
				reader: new TFChunkReader(_node.Db, _node.Db.Config.WriterCheckpoint),
				writer: writer),
			Guid.NewGuid());
		await epochManager.Init(CancellationToken.None);

		return epochManager.GetLastEpoch()?.EpochId;
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		_logFormat?.Dispose();
		await base.TestFixtureTearDown();
	}

	[Test]
	public void should_be_a_different_epoch_for_every_startup()
	{
		Assert.AreEqual(_numberOfNodeStarts, _epochIds.Distinct().Count());
	}
}
