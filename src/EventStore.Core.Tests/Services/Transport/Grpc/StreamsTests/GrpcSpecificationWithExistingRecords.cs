using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.LogRecords;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

public abstract class GrpcSpecificationWithExistingRecords<TLogFormat, TStreamId>
	: SpecificationWithDirectoryPerTestFixture
{
	private string _dbPath;
	private LogFormatAbstractor<TStreamId> _logFormatFactory;
	private TFChunkDb _db;
	private TFChunkWriter _writer;
	private ICheckpoint _writerCheckpoint;
	private ICheckpoint _chaserCheckpoint;

	protected MiniNode<TLogFormat, TStreamId> Node;
	protected GrpcChannel Channel;

	protected static (string userName, string password) AdminCredentials => ("admin", "changeit");

	public abstract ValueTask WriteTestScenario(CancellationToken token);

	public abstract Task Given();

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_dbPath = Path.Combine(PathName, $"mini-node-db-{Guid.NewGuid():N}");
		_logFormatFactory = LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory.Create(new()
		{
			IndexDirectory = GetFilePathFor("index")
		});

		Directory.CreateDirectory(_dbPath);

		_writerCheckpoint = new MemoryMappedFileCheckpoint(
			Path.Combine(_dbPath, Checkpoint.Writer + ".chk"), Checkpoint.Writer);
		_chaserCheckpoint = new MemoryMappedFileCheckpoint(
			Path.Combine(_dbPath, Checkpoint.Chaser + ".chk"), Checkpoint.Chaser);
		_db = new TFChunkDb(TFChunkHelper.CreateDbConfig(
			_dbPath, _writerCheckpoint, _chaserCheckpoint, TFConsts.ChunkSize));
		await _db.Open();

		_writer = new TFChunkWriter(_db);
		_writer.Open();
		var partitionManager = _logFormatFactory.CreatePartitionManager(
			new TFChunkReader(_db, _writerCheckpoint), _writer);
		await partitionManager.Initialize(CancellationToken.None);
		await WriteTestScenario(CancellationToken.None);

		await _writer.DisposeAsync();
		_writer = null;
		_writerCheckpoint.Flush();
		_chaserCheckpoint.Write(_writerCheckpoint.Read());
		_chaserCheckpoint.Flush();
		await _db.DisposeAsync();
		_db = null;

		Node = new MiniNode<TLogFormat, TStreamId>(PathName, dbPath: _dbPath);
		await Node.Start();
		await Node.AdminUserCreated;
		Channel = GrpcChannel.ForAddress(new UriBuilder { Scheme = Uri.UriSchemeHttps }.Uri,
			new GrpcChannelOptions
			{
				HttpClient = Node.HttpClient,
				DisposeHttpClient = false
			});

		await Given().WithTimeout(TimeSpan.FromSeconds(30));
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		Channel?.Dispose();
		_logFormatFactory?.Dispose();
		if (Node is not null)
		{
			await Node.Shutdown();
		}
		await base.TestFixtureTearDown();
	}

	protected CallOptions GetCallOptions((string userName, string password) credentials) =>
		new(credentials: CallCredentials.FromInterceptor((_, metadata) =>
		{
			var value = Convert.ToBase64String(
				Encoding.ASCII.GetBytes($"{credentials.userName}:{credentials.password}"));
			metadata.Add(new Metadata.Entry("authorization", $"Basic {value}"));
			return Task.CompletedTask;
		}));

	protected async ValueTask<EventRecord> WriteSingleEvent(
		string eventStreamName,
		long eventNumber,
		string data,
		Guid eventId = default,
		string eventType = "some-type",
		CancellationToken token = default)
	{
		var position = _writer.Position;
		_logFormatFactory.StreamNameIndex.GetOrReserve(
			_logFormatFactory.RecordFactory,
			eventStreamName,
			position,
			out var eventStreamId,
			out var streamRecord);
		if (streamRecord is not null)
		{
			(_, position) = await _writer.Write(streamRecord, token);
		}

		_logFormatFactory.EventTypeIndex.GetOrReserveEventType(
			_logFormatFactory.RecordFactory,
			eventType,
			position,
			out var eventTypeId,
			out var eventTypeRecord);
		if (eventTypeRecord is not null)
		{
			(_, position) = await _writer.Write(eventTypeRecord, token);
		}

		var prepare = LogRecord.SingleWrite(
			_logFormatFactory.RecordFactory,
			position,
			eventId == default ? Guid.NewGuid() : eventId,
			Guid.NewGuid(),
			eventStreamId,
			eventNumber - 1,
			eventTypeId,
			Helper.UTF8NoBom.GetBytes(data),
			null);
		var (written, nextPosition) = await _writer.Write(prepare, token);
		Assert.IsTrue(written);
		var commit = LogRecord.Commit(nextPosition, prepare.CorrelationId, prepare.LogPosition, eventNumber);
		Assert.IsTrue(await _writer.Write(commit, token) is (true, _));

		return new EventRecord(eventNumber, prepare, eventStreamName, eventType);
	}
}
