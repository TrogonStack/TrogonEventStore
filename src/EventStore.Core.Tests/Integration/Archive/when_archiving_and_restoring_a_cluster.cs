using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using EventStore.ClientAPI;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using NUnit.Framework;

namespace EventStore.Core.Tests.Integration.Archive;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[Category("ArchiveIntegration")]
[NonParallelizable]
public class when_archiving_and_restoring_a_cluster<TLogFormat, TStreamId>
	: specification_with_cluster<TLogFormat, TStreamId>
{
	private const string Stream = "archive-soak";
	private const string ArchiveCheckpointFile = "archive.chk";
	private const int ArchiverNodeIndex = 3;
	private const int SoakIterations = 3;
	private const int EventsPerIteration = 12;
	private static readonly TimeSpan GateTimeout = TimeSpan.FromMinutes(3);
	private static readonly TimeSpan SoakTimeout = TimeSpan.FromMinutes(15);

	private readonly string _bucket = $"archive-soak-{Guid.NewGuid():N}";
	private readonly ArchiveOptions _archiveOptions;
	private AmazonS3Client _s3Client;
	private S3Reader _archiveReader;
	private long _archivedCheckpoint;
	private int _restoredNodeIndex;
	private int _completedIterations;
	protected override int NodeCount => 4;
	protected override TimeSpan GivenTimeout => SoakTimeout;

	public when_archiving_and_restoring_a_cluster()
	{
		var endpoint = Environment.GetEnvironmentVariable("EVENTSTORE_S3_TEST_ENDPOINT");
		var region = Environment.GetEnvironmentVariable("EVENTSTORE_S3_TEST_REGION") ?? "us-east-1";
		var accessKey = Environment.GetEnvironmentVariable("EVENTSTORE_S3_TEST_ACCESS_KEY");
		var secretKey = Environment.GetEnvironmentVariable("EVENTSTORE_S3_TEST_SECRET_KEY");

		_archiveOptions = new()
		{
			Enabled = !string.IsNullOrWhiteSpace(endpoint),
			StorageType = StorageType.S3,
			S3 = new()
			{
				Bucket = _bucket,
				Region = region,
				AccessKeyId = accessKey ?? string.Empty,
				SecretAccessKey = secretKey ?? string.Empty,
				ServiceUrl = endpoint ?? string.Empty,
			},
			RetainAtLeast = new() { Days = 0, LogicalBytes = 0 },
		};
	}

	protected override void BeforeNodesStart()
	{
		if (!_archiveOptions.Enabled)
		{
			Assert.Ignore("The archive integration endpoint is not configured.");
		}

		_s3Client = new AmazonS3Client(
			_archiveOptions.S3.AccessKeyId,
			_archiveOptions.S3.SecretAccessKey,
			new AmazonS3Config
			{
				ServiceURL = _archiveOptions.S3.ServiceUrl,
				AuthenticationRegion = _archiveOptions.S3.Region,
				ForcePathStyle = true,
			});
		_s3Client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }).GetAwaiter().GetResult();
		var checkpointInitialized = new S3Writer(_archiveOptions.S3, ArchiveCheckpointFile)
			.SetCheckpoint(0L, CancellationToken.None)
			.AsTask()
			.GetAwaiter()
			.GetResult();
		if (!checkpointInitialized)
		{
			throw new InvalidOperationException("Failed to initialize the archive checkpoint.");
		}

		var archiveNamer = new ArchiveChunkNamer(
			new VersionedPatternFileNamingStrategy(PathName, "chunk-"));
		_archiveReader = new S3Reader(_archiveOptions.S3, archiveNamer, ArchiveCheckpointFile);
	}

	protected override MiniClusterNode<TLogFormat, TStreamId> CreateNode(
		int index,
		Endpoints endpoints,
		EndPoint[] gossipSeeds,
		bool wait = true) =>
		new(
			PathName,
			index,
			endpoints.InternalTcp,
			endpoints.ExternalTcp,
			endpoints.HttpEndPoint,
			gossipSeeds,
			readOnlyReplica: index == ArchiverNodeIndex,
			archiveOptions: _archiveOptions.Enabled ? _archiveOptions : null,
			archiver: index == ArchiverNodeIndex);

	protected override IEventStoreConnection CreateConnection() =>
		EventStoreConnection.Create(
			ConnectionSettings.Create().DisableServerCertificateValidation(),
			GetLeader().ExternalTcpEndPoint);

	protected override async Task Given()
	{
		var payload = new byte[256 * 1024];
		new Random(1729).NextBytes(payload);
		var leader = GetLeader();
		var archiver = _nodes[ArchiverNodeIndex];
		AssertEx.IsOrBecomesTrue(
			() => _nodes.Any(node =>
				node.DebugIndex != ArchiverNodeIndex && node.NodeState == VNodeState.Follower),
			GateTimeout,
			$"A voting follower did not become ready. States={string.Join(", ", _nodes.Select(node => node.NodeState))}");
		_restoredNodeIndex = Array.FindIndex(_nodes,
			node => node.NodeState == VNodeState.Follower && node.DebugIndex != ArchiverNodeIndex);
		Assert.That(_restoredNodeIndex, Is.GreaterThanOrEqualTo(0));

		for (var iteration = 0; iteration < SoakIterations; iteration++)
		{
			for (var eventNumber = 0; eventNumber < EventsPerIteration; eventNumber++)
			{
				await _conn.AppendToStreamAsync(
					Stream,
					EventStore.ClientAPI.ExpectedVersion.Any,
					new EventData(Guid.NewGuid(), "archive-event", isJson: false, payload, Array.Empty<byte>()));
			}

			AssertEx.IsOrBecomesTrue(
				() => archiver.Db.Config.WriterCheckpoint.Read() >= leader.Db.Config.WriterCheckpoint.Read(),
				GateTimeout,
				$"The archiver did not replicate iteration {iteration + 1}.");

			var previousCheckpoint = _archivedCheckpoint;
			await WaitForArchiveCheckpoint(previousCheckpoint + 2L * MiniNode.ChunkSize);
			_archivedCheckpoint = await _archiveReader.GetCheckpoint(CancellationToken.None);
			var coldChunkNumber = (int)(previousCheckpoint / MiniNode.ChunkSize);

			await StartScavenge(leader);
			AssertEx.IsOrBecomesTrue(
				() => leader.Db.Manager.GetChunk(coldChunkNumber).IsRemote,
				GateTimeout,
				$"Iteration {iteration + 1} did not replace chunk {coldChunkNumber} with an archive locator.");
			Assert.That(
				(await leader.Db.Manager.GetChunk(coldChunkNumber).TryReadFirst(CancellationToken.None)).Success,
				Is.True);

			await RestoreNode(_restoredNodeIndex, coldChunkNumber);
			_completedIterations++;
		}
	}

	private static async Task StartScavenge(MiniClusterNode<TLogFormat, TStreamId> leader)
	{
		var scavengeStarted = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
		leader.Node.MainQueue.Publish(new ClientMessage.ScavengeDatabase(
			new CallbackEnvelope(scavengeStarted.SetResult),
			Guid.NewGuid(),
			SystemAccounts.System,
			startFromChunk: 0,
			threads: 1,
			threshold: null,
			throttlePercent: null,
			syncOnly: false));
		Assert.That(await scavengeStarted.Task.WaitAsync(GateTimeout),
			Is.TypeOf<ClientMessage.ScavengeDatabaseStartedResponse>());
	}

	private async Task RestoreNode(int nodeIndex, int coldChunkNumber)
	{
		await _nodes[nodeIndex].Shutdown(keepDb: true);
		Directory.Delete(_nodes[nodeIndex].DbPath, recursive: true);

		var restored = CreateNode(nodeIndex, _nodeEndpoints[nodeIndex], GossipSeedsFor(nodeIndex));
		restored.Start();
		_nodes[nodeIndex] = restored;
		await restored.Started.WaitAsync(GateTimeout);

		AssertEx.IsOrBecomesTrue(
			() => restored.Db.Config.WriterCheckpoint.Read() >= _archivedCheckpoint,
			GateTimeout,
			"The restored node did not catch up from the archive before rejoining replication.");
		Assert.That(
			(await restored.Db.Manager.GetChunk(coldChunkNumber).TryReadFirst(CancellationToken.None)).Success,
			Is.True);
	}

	private EndPoint[] GossipSeedsFor(int nodeIndex) =>
		_nodeEndpoints
			.Where((_, index) => index != nodeIndex)
			.Select(x => (EndPoint)x.HttpEndPoint)
			.ToArray();

	private async Task WaitForArchiveCheckpoint(long minimum)
	{
		using var timeout = new CancellationTokenSource(GateTimeout);
		while (await _archiveReader.GetCheckpoint(timeout.Token) < minimum)
		{
			await Task.Delay(100, timeout.Token);
		}
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		await base.TestFixtureTearDown();
		if (_s3Client is null)
		{
			return;
		}

		var objects = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket });
		foreach (var item in objects.S3Objects)
		{
			await _s3Client.DeleteObjectAsync(_bucket, item.Key);
		}
		await _s3Client.DeleteBucketAsync(_bucket);
		_s3Client.Dispose();
	}

	[Test]
	public void archive_restore_gate_completed()
	{
		Assert.That(_completedIterations, Is.EqualTo(SoakIterations));
		Assert.That(_archivedCheckpoint, Is.GreaterThanOrEqualTo(SoakIterations * 2L * MiniNode.ChunkSize));
		Assert.That(_nodes[ArchiverNodeIndex].NodeState, Is.EqualTo(VNodeState.ReadOnlyReplica));
		Assert.That(_nodes[_restoredNodeIndex].NodeState, Is.AnyOf(VNodeState.Follower, VNodeState.Leader));
	}
}
