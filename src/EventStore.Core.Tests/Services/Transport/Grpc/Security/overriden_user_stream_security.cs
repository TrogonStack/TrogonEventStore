using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class overriden_user_stream_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	protected override async Task Given()
	{
		await base.Given();
		await SetDefaultAcl(StreamKind.User, StreamAclKind.UserOne);
	}

	[Test]
	public async Task operations_on_user_stream_succeeds_for_authorized_user() =>
		await AssertOperations("user-authorized-user", SecurityIdentity.UserOne, StatusCode.OK);

	[Test]
	public async Task operations_on_user_stream_fail_for_not_authorized_user() =>
		await AssertOperations("user-not-authorized", SecurityIdentity.UserTwo, StatusCode.PermissionDenied);

	[Test]
	public async Task operations_on_user_stream_fail_for_anonymous_user() =>
		await AssertOperations("user-anonymous-user", SecurityIdentity.Anonymous, StatusCode.PermissionDenied);

	[Test]
	public async Task operations_on_user_stream_succeed_for_admin() =>
		await AssertOperations("user-admin", SecurityIdentity.Admin, StatusCode.OK);

	private async Task AssertOperations(
		string streamName,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		AssertStatus(expectedStatus, await ExecuteBatchAppend(streamName, identity));
		await AssertAllStreamOperations(streamName, identity, expectedStatus);
	}
}
