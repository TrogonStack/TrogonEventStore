using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Index;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[Category("LongRunning")]
public class HashCollisionGrpcBoundaryTests : SpecificationWithDirectoryPerTestFixture
{
	private const string FirstStream = "account--696193173";
	private const string SecondStream = "LPN-FC002_LPK51001";
	private MiniNode<LogFormat.V2, string> _node;
	private GrpcStreamEdgeOperations _grpc;
	private string _dbPath;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_dbPath = Path.Combine(PathName, "collision-node-db");
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
	public async Task does_not_return_a_colliding_stream_after_the_read_limit_is_reached()
	{
		AssertSuccess(await _grpc.Append(FirstStream, noStream: true), 0);
		AssertSuccess(await _grpc.Append(SecondStream, count: 100), 99);

		await _node.Shutdown(keepDb: true);
		_grpc.Dispose();
		await StartNode(waitForAdminUserCreation: false);

		var firstRead = await _grpc.Read(FirstStream, 0, 1);
		Assert.That(firstRead.Single().ContentCase,
			Is.EqualTo(ReadResp.ContentOneofCase.StreamNotFound));

		var secondRead = await _grpc.Read(SecondStream, 99, 1);
		Assert.That(secondRead.Single(x => x.Event is not null).Event.Event.StreamRevision,
			Is.EqualTo(99));

		var append = await _grpc.AppendSingle(FirstStream);
		Assert.That(append.ResultCase, Is.EqualTo(AppendResp.ResultOneofCase.WrongExpectedVersion));
		Assert.That(append.WrongExpectedVersion.CurrentRevisionOptionCase,
			Is.EqualTo(AppendResp.Types.WrongExpectedVersion.CurrentRevisionOptionOneofCase.None));

		var batchAppend = await _grpc.Append(FirstStream);
		Assert.That(batchAppend.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Error));
		Assert.That(batchAppend.Error.Code, Is.EqualTo(Google.Rpc.Code.AlreadyExists));
		var error = batchAppend.Error.Details.Unpack<EventStore.Client.WrongExpectedVersion>();
		Assert.That(error.CurrentStreamRevisionOptionCase,
			Is.EqualTo(EventStore.Client.WrongExpectedVersion.CurrentStreamRevisionOptionOneofCase.None));
	}

	private async Task StartNode(bool waitForAdminUserCreation)
	{
		_node = new MiniNode<LogFormat.V2, string>(PathName,
			dbPath: _dbPath,
			memTableSize: 20,
			hashCollisionReadLimit: 1,
			indexBitnessVersion: PTableVersions.IndexV4,
			hash32bit: true,
			streamExistenceFilterSize: 0);
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
