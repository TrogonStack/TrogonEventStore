using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class overriden_system_stream_security_for_all<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	protected override async Task Given()
	{
		await base.Given();
		await SetDefaultAcl(StreamKind.System, StreamAclKind.All);
	}

	[Test]
	public async Task operations_on_system_stream_succeeds_for_user() =>
		await AssertOperations("$sys-authorized-user", SecurityIdentity.UserOne);

	[Test]
	public async Task operations_on_system_stream_fail_for_anonymous_user() =>
		await AssertOperations("$sys-anonymous-user", SecurityIdentity.Anonymous);

	[Test]
	public async Task operations_on_system_stream_succeed_for_admin() =>
		await AssertOperations("$sys-admin", SecurityIdentity.Admin);

	private async Task AssertOperations(string streamName, SecurityIdentity identity)
	{
		AssertStatus(StatusCode.OK, await ExecuteBatchAppend(streamName, identity));
		await AssertAllStreamOperations(streamName, identity, StatusCode.OK);
	}
}
