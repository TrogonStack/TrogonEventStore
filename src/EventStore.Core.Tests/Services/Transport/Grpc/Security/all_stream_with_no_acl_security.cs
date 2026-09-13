using System.Threading.Tasks;
using EventStore.Core.Services;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class all_stream_with_no_acl_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	protected override async Task Given()
	{
		await base.Given();
		await SetStreamAcl(SystemStreams.AllStream, "{}");
	}

	[Test]
	public async Task write_to_all_is_never_allowed()
	{
		await AssertOperation(SecurityOperation.Write, SecurityIdentity.Anonymous, StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.Write, SecurityIdentity.UserOne, StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.Write, SecurityIdentity.Admin, StatusCode.PermissionDenied);
	}

	[Test]
	public async Task delete_of_all_is_never_allowed()
	{
		await AssertOperation(SecurityOperation.Tombstone, SecurityIdentity.Anonymous, StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.Tombstone, SecurityIdentity.UserOne, StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.Tombstone, SecurityIdentity.Admin, StatusCode.PermissionDenied);
	}

	[Test]
	public async Task reading_and_subscribing_is_not_allowed_when_no_credentials_are_passed() =>
		await AssertReadAndSubscribe(SecurityIdentity.Anonymous, StatusCode.PermissionDenied);

	[Test]
	public async Task reading_and_subscribing_is_not_allowed_for_usual_user() =>
		await AssertReadAndSubscribe(SecurityIdentity.UserOne, StatusCode.PermissionDenied);

	[Test]
	public async Task reading_and_subscribing_is_allowed_for_admin_user() =>
		await AssertReadAndSubscribe(SecurityIdentity.Admin, StatusCode.OK);

	[Test]
	public async Task meta_write_is_not_allowed_when_no_credentials_are_passed() =>
		await AssertOperation(SecurityOperation.WriteMetadata, SecurityIdentity.Anonymous, StatusCode.PermissionDenied);

	[Test]
	public async Task meta_write_is_not_allowed_for_usual_user() =>
		await AssertOperation(SecurityOperation.WriteMetadata, SecurityIdentity.UserOne, StatusCode.PermissionDenied);

	[Test]
	public async Task meta_write_is_allowed_for_admin_user() =>
		await AssertOperation(SecurityOperation.WriteMetadata, SecurityIdentity.Admin, StatusCode.OK);

	private async Task AssertReadAndSubscribe(SecurityIdentity identity, StatusCode expectedStatus)
	{
		await AssertOperation(SecurityOperation.ReadEvent, identity, expectedStatus);
		AssertStatus(expectedStatus, await ExecuteAllOperation(false, identity));
		AssertStatus(expectedStatus, await ExecuteAllOperation(false, identity, backwards: true));
		await AssertOperation(SecurityOperation.ReadMetadata, identity, expectedStatus);
		AssertStatus(expectedStatus, await ExecuteAllOperation(true, identity));
	}

	private async Task AssertOperation(
		SecurityOperation operation,
		SecurityIdentity identity,
		StatusCode expectedStatus) =>
		AssertStatus(expectedStatus, await ExecuteOperation(operation, SystemStreams.AllStream, identity));
}
