using System;
using System.Threading.Tasks;
using EventStore.Core.Services;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class authorized_default_credentials_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	public authorized_default_credentials_security() : base(SecurityIdentity.UserOne)
	{
	}

	[Test]
	public async Task all_operations_succeeds_when_passing_no_explicit_credentials()
	{
		var streams = await CreateProtectedStreams();

		AssertStatus(StatusCode.OK, await ExecuteAllOperationWithDefault(false));
		AssertStatus(StatusCode.OK, await ExecuteAllOperationWithDefault(false, backwards: true));
		await AssertDefaultOperation(SecurityOperation.ReadEvent, streams.Read, StatusCode.OK);
		await AssertDefaultOperation(SecurityOperation.ReadForward, streams.Read, StatusCode.OK);
		await AssertDefaultOperation(SecurityOperation.ReadBackward, streams.Read, StatusCode.OK);
		await AssertDefaultOperation(SecurityOperation.Write, streams.Write, StatusCode.OK);
		AssertStatus(StatusCode.OK, await ExecuteBatchAppendWithDefault(streams.Write));
		await AssertDefaultOperation(SecurityOperation.ReadMetadata, streams.MetadataRead, StatusCode.OK);
		await AssertDefaultOperation(SecurityOperation.WriteMetadata, streams.MetadataWrite, StatusCode.OK);
		await AssertDefaultOperation(SecurityOperation.Subscribe, streams.Read, StatusCode.OK);
		AssertStatus(StatusCode.OK, await ExecuteAllOperationWithDefault(true));
	}

	[Test]
	public async Task all_operations_are_not_authenticated_when_overriden_with_not_existing_credentials()
	{
		var streams = await CreateProtectedStreams();
		await AssertExplicitOperations(streams, SecurityIdentity.Invalid, StatusCode.Unauthenticated);
	}

	[Test]
	public async Task all_operations_are_not_authorized_when_overriden_with_not_authorized_credentials()
	{
		var streams = await CreateProtectedStreams();
		await AssertExplicitOperations(streams, SecurityIdentity.UserTwo, StatusCode.PermissionDenied);
	}

	private async Task<ProtectedStreams> CreateProtectedStreams()
	{
		var suffix = Guid.NewGuid().ToString("N");
		var streams = new ProtectedStreams(
			$"read-stream-{suffix}",
			$"write-stream-{suffix}",
			$"metaread-stream-{suffix}",
			$"metawrite-stream-{suffix}");
		await SetStreamAcl(streams.Read, $"{{\"$acl\":{{\"$r\":\"{UserOneName}\"}}}}");
		await SetStreamAcl(streams.Write, $"{{\"$acl\":{{\"$w\":\"{UserOneName}\"}}}}");
		await SetStreamAcl(streams.MetadataRead, $"{{\"$acl\":{{\"$mr\":\"{UserOneName}\"}}}}");
		await SetStreamAcl(streams.MetadataWrite, $"{{\"$acl\":{{\"$mw\":\"{UserOneName}\"}}}}");
		await SetStreamAcl(SystemStreams.AllStream, $"{{\"$acl\":{{\"$r\":\"{UserOneName}\"}}}}");
		return streams;
	}

	private async Task AssertExplicitOperations(
		ProtectedStreams streams,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		AssertStatus(expectedStatus, await ExecuteAllOperation(false, identity));
		AssertStatus(expectedStatus, await ExecuteAllOperation(false, identity, backwards: true));
		await AssertOperation(SecurityOperation.ReadEvent, streams.Read, identity, expectedStatus);
		await AssertOperation(SecurityOperation.ReadForward, streams.Read, identity, expectedStatus);
		await AssertOperation(SecurityOperation.ReadBackward, streams.Read, identity, expectedStatus);
		await AssertOperation(SecurityOperation.Write, streams.Write, identity, expectedStatus);
		AssertStatus(expectedStatus, await ExecuteBatchAppend(streams.Write, identity));
		await AssertOperation(SecurityOperation.ReadMetadata, streams.MetadataRead, identity, expectedStatus);
		await AssertOperation(SecurityOperation.WriteMetadata, streams.MetadataWrite, identity, expectedStatus);
		await AssertOperation(SecurityOperation.Subscribe, streams.Read, identity, expectedStatus);
		AssertStatus(expectedStatus, await ExecuteAllOperation(true, identity));
	}

	private async Task AssertDefaultOperation(
		SecurityOperation operation,
		string streamName,
		StatusCode expectedStatus) =>
		AssertStatus(expectedStatus, await ExecuteOperationWithDefault(operation, streamName));

	private async Task AssertOperation(
		SecurityOperation operation,
		string streamName,
		SecurityIdentity identity,
		StatusCode expectedStatus) =>
		AssertStatus(expectedStatus, await ExecuteOperation(operation, streamName, identity));

	private readonly record struct ProtectedStreams(
		string Read,
		string Write,
		string MetadataRead,
		string MetadataWrite);
}
