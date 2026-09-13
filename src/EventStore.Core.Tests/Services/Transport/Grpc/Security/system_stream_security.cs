using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class system_stream_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	public static IEnumerable<TestCaseData> Cases()
	{
		yield return Case(StreamAclKind.None, StatusCode.PermissionDenied,
			"operations_on_system_stream_with_no_acl_set_fail_for_non_admin",
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.None, StatusCode.OK,
			"operations_on_system_stream_with_no_acl_set_succeed_for_admin", SecurityIdentity.Admin);

		yield return Case(StreamAclKind.UserOne, StatusCode.PermissionDenied,
			"operations_on_system_stream_with_acl_set_to_usual_user_fail_for_not_authorized_user",
			SecurityIdentity.Anonymous, SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.UserOne, StatusCode.OK,
			"operations_on_system_stream_with_acl_set_to_usual_user_succeed_for_that_user", SecurityIdentity.UserOne);
		yield return Case(StreamAclKind.UserOne, StatusCode.OK,
			"operations_on_system_stream_with_acl_set_to_usual_user_succeed_for_admin", SecurityIdentity.Admin);

		yield return Case(StreamAclKind.Admins, StatusCode.PermissionDenied,
			"operations_on_system_stream_with_acl_set_to_admins_fail_for_usual_user",
			SecurityIdentity.Anonymous, SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.Admins, StatusCode.OK,
			"operations_on_system_stream_with_acl_set_to_admins_succeed_for_admin", SecurityIdentity.Admin);

		yield return Case(StreamAclKind.All, StatusCode.OK,
			"operations_on_system_stream_with_acl_set_to_all_succeed_for_not_authenticated_user",
			SecurityIdentity.Anonymous);
		yield return Case(StreamAclKind.All, StatusCode.OK,
			"operations_on_system_stream_with_acl_set_to_all_succeed_for_usual_user",
			SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.All, StatusCode.OK,
			"operations_on_system_stream_with_acl_set_to_all_succeed_for_admin", SecurityIdentity.Admin);

		foreach (var acl in new[]
			{
				StreamAclKind.None,
				StreamAclKind.UserOne,
				StreamAclKind.Admins,
				StreamAclKind.All
			})
		{
			yield return Case(acl, StatusCode.Unauthenticated,
				$"operations_on_system_stream_with_{acl.ToString().ToLowerInvariant()}_acl_reject_invalid_credentials",
				SecurityIdentity.Invalid);
		}
	}

	[TestCaseSource(nameof(Cases))]
	public async Task enforces_system_stream_security(
		StreamAclKind acl,
		StatusCode expectedStatus,
		SecurityIdentity[] identities)
	{
		foreach (var identity in identities)
		{
			await AssertAllStreamOperations(StreamKind.System, acl, identity, expectedStatus);
		}
	}

	private static TestCaseData Case(
		StreamAclKind acl,
		StatusCode status,
		string name,
		params SecurityIdentity[] identities) =>
		new TestCaseData(acl, status, identities).SetName(name);
}
