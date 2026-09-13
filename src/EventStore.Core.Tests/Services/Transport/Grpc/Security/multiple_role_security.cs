using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class multiple_role_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	protected override async Task Given()
	{
		await base.Given();
		await SetSystemSettings(
			"{\"$userStreamAcl\":{\"$r\":[\"user1\",\"user2\"],\"$w\":[\"$admins\",\"user1\"],\"$d\":[\"user1\",\"$all\"]}}");
	}

	[Test]
	public async Task multiple_roles_are_handled_correctly()
	{
		await AssertOperation(SecurityOperation.ReadEvent, "usr-stream", SecurityIdentity.Anonymous,
			StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.ReadEvent, "usr-stream", SecurityIdentity.UserOne, StatusCode.OK);
		await AssertOperation(SecurityOperation.ReadEvent, "usr-stream", SecurityIdentity.UserTwo, StatusCode.OK);
		await AssertOperation(SecurityOperation.ReadEvent, "usr-stream", SecurityIdentity.Admin, StatusCode.OK);

		await AssertOperation(SecurityOperation.Write, "usr-stream", SecurityIdentity.Anonymous,
			StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.Write, "usr-stream", SecurityIdentity.UserOne, StatusCode.OK);
		await AssertOperation(SecurityOperation.Write, "usr-stream", SecurityIdentity.UserTwo,
			StatusCode.PermissionDenied);
		await AssertOperation(SecurityOperation.Write, "usr-stream", SecurityIdentity.Admin, StatusCode.OK);

		await AssertOperation(SecurityOperation.Tombstone, "usr-stream1", SecurityIdentity.Anonymous, StatusCode.OK);
		await AssertOperation(SecurityOperation.Tombstone, "usr-stream2", SecurityIdentity.UserOne, StatusCode.OK);
		await AssertOperation(SecurityOperation.Tombstone, "usr-stream3", SecurityIdentity.UserTwo, StatusCode.OK);
		await AssertOperation(SecurityOperation.Tombstone, "usr-stream4", SecurityIdentity.Admin, StatusCode.OK);
	}

	private async Task AssertOperation(
		SecurityOperation operation,
		string streamName,
		SecurityIdentity identity,
		StatusCode expectedStatus) =>
		AssertStatus(expectedStatus, await ExecuteOperation(operation, streamName, identity));
}
