using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class overriden_system_stream_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	protected override async Task Given()
	{
		await base.Given();
		await SetDefaultAcl(StreamKind.System, StreamAclKind.UserOne);
	}

	[Test]
	public async Task operations_on_system_stream_succeed_for_authorized_user() =>
		await AssertOperations("$sys-authorized-user", SecurityIdentity.UserOne, StatusCode.OK);

	[Test]
	public async Task operations_on_system_stream_fail_for_not_authorized_user() =>
		await AssertOperations("$sys-not-authorized-user", SecurityIdentity.UserTwo, StatusCode.PermissionDenied);

	[Test]
	public async Task operations_on_system_stream_fail_for_anonymous_user() =>
		await AssertOperations("$sys-anonymous-user", SecurityIdentity.Anonymous, StatusCode.PermissionDenied);

	[Test]
	public async Task operations_on_system_stream_succeed_for_admin() =>
		await AssertOperations("$sys-admin", SecurityIdentity.Admin, StatusCode.OK);

	private async Task AssertOperations(
		string streamName,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		AssertStatus(expectedStatus, await ExecuteBatchAppend(streamName, identity));
		await AssertAllStreamOperations(streamName, identity, expectedStatus);
	}
}
