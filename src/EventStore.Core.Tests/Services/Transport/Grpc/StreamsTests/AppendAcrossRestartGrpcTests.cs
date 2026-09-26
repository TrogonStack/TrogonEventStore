using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[Category("LongRunning")]
public class AppendAcrossRestartGrpcTests : SpecificationWithDirectoryPerTestFixture
{
	private MiniNode<LogFormat.V2, string> _node;
	private GrpcStreamEdgeOperations _grpc;
	private string _dbPath;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_dbPath = Path.Combine(PathName, "restart-node-db");
		await StartNode(waitForAdminUserCreation: true);
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		_grpc?.Dispose();
		if (_node is not null)
			await _node.Shutdown();
		await base.TestFixtureTearDown();
	}

	[Test]
	public async Task detects_existing_streams_and_metadata_after_restart()
	{
		const string stream = "grpc-existing-stream-across-restart";
		const string metadataStream = "$$grpc-metadata-across-restart";
		AssertSuccess(await _grpc.Append(stream, count: 10, noStream: true), 9);
		AssertSuccess(await _grpc.Append(metadataStream, noStream: true,
			data: "{\"$maxCount\":5}", eventType: "$metadata"), 0);
		AssertSuccess(await _grpc.Append("grpc-last-stream-before-restart", noStream: true), 0);

		await Task.Delay(500);
		await _node.Shutdown(keepDb: true);
		_grpc.Dispose();
		await StartNode(waitForAdminUserCreation: false);

		AssertSuccess(await _grpc.Append(stream, expectedRevision: 9), 10);
		AssertSuccess(await _grpc.Append(metadataStream, expectedRevision: 0,
			data: "{\"$maxCount\":6}", eventType: "$metadata"), 1);

		var events = await _grpc.Read(stream, 0, 20);
		Assert.That(events.Count(x => x.Event is not null), Is.EqualTo(11));
		Assert.That(events.Last(x => x.Event is not null).Event.Event.StreamRevision,
			Is.EqualTo(10));
	}

	private async Task StartNode(bool waitForAdminUserCreation)
	{
		_node = new MiniNode<LogFormat.V2, string>(PathName,
			dbPath: _dbPath,
			streamExistenceFilterSize: 10_000,
			streamExistenceFilterCheckpointIntervalMs: 100,
			streamExistenceFilterCheckpointDelayMs: 0);
		await _node.Start();
		if (waitForAdminUserCreation)
			await _node.AdminUserCreated;
		_grpc = new GrpcStreamEdgeOperations(_node);
	}

	private static void AssertSuccess(BatchAppendResp response, ulong expectedRevision)
	{
		Assert.That(response.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
		Assert.That(response.Success.CurrentRevision, Is.EqualTo(expectedRevision));
	}
}
