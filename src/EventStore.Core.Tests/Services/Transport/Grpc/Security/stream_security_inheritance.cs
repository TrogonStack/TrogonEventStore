using System;
using System.Threading.Tasks;
using EventStore.Core.Services;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class stream_security_inheritance<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	[Test]
	public async Task acl_inheritance_is_working_properly_on_user_streams()
	{
		await SetWriteDefaults();

		var inherited = StreamName();
		await AssertOperation(inherited, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserTwo);
		await AssertOperation(inherited, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.UserOne, SecurityIdentity.Admin);

		var differentRole = StreamName();
		await SetStreamAcl(differentRole, WriteAclJson(UserTwoName));
		await AssertOperation(differentRole, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne);
		await AssertOperation(differentRole, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		var multipleRoles = StreamName();
		await SetStreamAcl(multipleRoles, WriteAclJson(UserOneName, UserTwoName));
		await AssertOperation(multipleRoles, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous);
		await AssertOperation(multipleRoles, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.UserOne, SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		var restricted = StreamName();
		await SetStreamAcl(restricted, WriteAclJson());
		await AssertOperation(restricted, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		await AssertOperation(restricted, SecurityOperation.Write, StatusCode.OK, SecurityIdentity.Admin);

		var all = StreamName();
		await SetStreamAcl(all, WriteAclJson(SystemRoles.All));
		await AssertOperation(all, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		await AssertOperation(inherited, SecurityOperation.ReadEvent, StatusCode.OK,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		var readRestricted = StreamName();
		await SetStreamAcl(readRestricted, $"{{\"$acl\":{{\"$r\":\"{UserOneName}\"}}}}");
		await AssertOperation(readRestricted, SecurityOperation.Write, StatusCode.OK, SecurityIdentity.UserOne);
		await AssertOperation(readRestricted, SecurityOperation.ReadEvent, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserTwo);
		await AssertOperation(readRestricted, SecurityOperation.ReadEvent, StatusCode.OK,
			SecurityIdentity.UserOne, SecurityIdentity.Admin);
	}

	[Test]
	public async Task acl_inheritance_is_working_properly_on_system_streams()
	{
		await SetWriteDefaults();

		var inherited = StreamName(system: true);
		await AssertOperation(inherited, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserTwo);
		await AssertOperation(inherited, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.UserOne, SecurityIdentity.Admin);

		var differentRole = StreamName(system: true);
		await SetStreamAcl(differentRole, WriteAclJson(UserTwoName));
		await AssertOperation(differentRole, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne);
		await AssertOperation(differentRole, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		var multipleRoles = StreamName(system: true);
		await SetStreamAcl(multipleRoles, WriteAclJson(UserOneName, UserTwoName));
		await AssertOperation(multipleRoles, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous);
		await AssertOperation(multipleRoles, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.UserOne, SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		var restricted = StreamName(system: true);
		await SetStreamAcl(restricted, WriteAclJson());
		await AssertOperation(restricted, SecurityOperation.Write, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		await AssertOperation(restricted, SecurityOperation.Write, StatusCode.OK, SecurityIdentity.Admin);

		var all = StreamName(system: true);
		await SetStreamAcl(all, WriteAclJson(SystemRoles.All));
		await AssertOperation(all, SecurityOperation.Write, StatusCode.OK,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo, SecurityIdentity.Admin);

		await AssertOperation(inherited, SecurityOperation.ReadEvent, StatusCode.PermissionDenied,
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		await AssertOperation(inherited, SecurityOperation.ReadEvent, StatusCode.OK, SecurityIdentity.Admin);
	}

	private async Task SetWriteDefaults() =>
		await SetSystemSettings(
			$"{{\"$userStreamAcl\":{{\"$w\":\"{UserOneName}\"}},\"$systemStreamAcl\":{{\"$w\":\"{UserOneName}\"}}}}");

	private async Task AssertOperation(
		string streamName,
		SecurityOperation operation,
		StatusCode expectedStatus,
		params SecurityIdentity[] identities)
	{
		foreach (var identity in identities)
		{
			AssertStatus(expectedStatus, await ExecuteOperation(operation, streamName, identity));
		}
	}

	private static string StreamName(bool system = false) =>
		$"{(system ? "$" : string.Empty)}grpc-security-inheritance-{Guid.NewGuid():N}";
}
