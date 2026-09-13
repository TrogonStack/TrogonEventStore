using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class read_stream_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	public static IEnumerable<TestCaseData> Cases()
	{
		yield return Case(StreamAclKind.UserOne, StatusCode.Unauthenticated,
			"reading_stream_with_not_existing_credentials_is_not_authenticated", SecurityIdentity.Invalid);
		yield return Case(StreamAclKind.UserOne, StatusCode.PermissionDenied,
			"reading_stream_with_no_credentials_is_denied", SecurityIdentity.Anonymous);
		yield return Case(StreamAclKind.UserOne, StatusCode.PermissionDenied,
			"reading_stream_with_not_authorized_user_credentials_is_denied", SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.UserOne, StatusCode.OK,
			"reading_stream_with_authorized_user_credentials_succeeds", SecurityIdentity.UserOne);
		yield return Case(StreamAclKind.UserOne, StatusCode.OK,
			"reading_stream_with_admin_user_credentials_succeeds", SecurityIdentity.Admin);

		yield return Case(StreamAclKind.None, StatusCode.OK,
			"reading_no_acl_stream_succeeds_when_no_credentials_are_passed", SecurityIdentity.Anonymous);
		yield return Case(StreamAclKind.None, StatusCode.Unauthenticated,
			"reading_no_acl_stream_is_not_authenticated_when_not_existing_credentials_are_passed",
			SecurityIdentity.Invalid);
		yield return Case(StreamAclKind.None, StatusCode.OK,
			"reading_no_acl_stream_succeeds_when_any_existing_user_credentials_are_passed",
			SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.None, StatusCode.OK,
			"reading_no_acl_stream_succeeds_when_admin_user_credentials_are_passed", SecurityIdentity.Admin);

		yield return Case(StreamAclKind.All, StatusCode.OK,
			"reading_all_access_normal_stream_succeeds_when_no_credentials_are_passed", SecurityIdentity.Anonymous);
		yield return Case(StreamAclKind.All, StatusCode.Unauthenticated,
			"reading_all_access_normal_stream_is_not_authenticated_when_not_existing_credentials_are_passed",
			SecurityIdentity.Invalid);
		yield return Case(StreamAclKind.All, StatusCode.OK,
			"reading_all_access_normal_stream_succeeds_when_any_existing_user_credentials_are_passed",
			SecurityIdentity.UserOne, SecurityIdentity.UserTwo);
		yield return Case(StreamAclKind.All, StatusCode.OK,
			"reading_all_access_normal_stream_succeeds_when_admin_user_credentials_are_passed", SecurityIdentity.Admin);
	}

	[TestCaseSource(nameof(Cases))]
	public async Task enforces_stream_read_security(
		StreamAclKind acl,
		StatusCode expectedStatus,
		SecurityIdentity[] identities)
	{
		foreach (var identity in identities)
		{
			var streamName = await CreateStreamWithAcl(StreamKind.User, acl);
			AssertStatus(expectedStatus, await ExecuteOperation(SecurityOperation.ReadEvent, streamName, identity));
			AssertStatus(expectedStatus, await ExecuteOperation(SecurityOperation.ReadForward, streamName, identity));
			AssertStatus(expectedStatus, await ExecuteOperation(SecurityOperation.ReadBackward, streamName, identity));
		}
	}

	private static TestCaseData Case(
		StreamAclKind acl,
		StatusCode status,
		string name,
		params SecurityIdentity[] identities) =>
		new TestCaseData(acl, status, identities).SetName(name);
}
