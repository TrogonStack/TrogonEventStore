using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventStore.Core.Services;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class delete_stream_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	public static IEnumerable<TestCaseData> DeleteCases()
	{
		yield return Case(StreamKind.User, StreamAclKind.None, SecurityIdentity.Anonymous, StatusCode.OK,
			"deleting_normal_no_acl_stream_with_no_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.None, SecurityIdentity.UserOne, StatusCode.OK,
			"deleting_normal_no_acl_stream_with_existing_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.None, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_normal_no_acl_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.UserOne, SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"deleting_normal_user_stream_with_no_user_is_not_allowed");
		yield return Case(StreamKind.User, StreamAclKind.UserOne, SecurityIdentity.UserTwo, StatusCode.PermissionDenied,
			"deleting_normal_user_stream_with_not_authorized_user_is_not_allowed");
		yield return Case(StreamKind.User, StreamAclKind.UserOne, SecurityIdentity.UserOne, StatusCode.OK,
			"deleting_normal_user_stream_with_authorized_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.UserOne, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_normal_user_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.Admins, SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"deleting_normal_admin_stream_with_no_user_is_not_allowed");
		yield return Case(StreamKind.User, StreamAclKind.Admins, SecurityIdentity.UserOne, StatusCode.PermissionDenied,
			"deleting_normal_admin_stream_with_existing_user_is_not_allowed");
		yield return Case(StreamKind.User, StreamAclKind.Admins, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_normal_admin_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.All, SecurityIdentity.Anonymous, StatusCode.OK,
			"deleting_normal_all_stream_with_no_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.All, SecurityIdentity.UserOne, StatusCode.OK,
			"deleting_normal_all_stream_with_existing_user_is_allowed");
		yield return Case(StreamKind.User, StreamAclKind.All, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_normal_all_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.None, SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"deleting_system_no_acl_stream_with_no_user_is_not_allowed");
		yield return Case(StreamKind.System, StreamAclKind.None, SecurityIdentity.UserOne, StatusCode.PermissionDenied,
			"deleting_system_no_acl_stream_with_existing_user_is_not_allowed");
		yield return Case(StreamKind.System, StreamAclKind.None, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_system_no_acl_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.UserOne, SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"deleting_system_user_stream_with_no_user_is_not_allowed");
		yield return Case(StreamKind.System, StreamAclKind.UserOne, SecurityIdentity.UserTwo, StatusCode.PermissionDenied,
			"deleting_system_user_stream_with_not_authorized_user_is_not_allowed");
		yield return Case(StreamKind.System, StreamAclKind.UserOne, SecurityIdentity.UserOne, StatusCode.OK,
			"deleting_system_user_stream_with_authorized_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.UserOne, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_system_user_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.Admins, SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"deleting_system_admin_stream_with_no_user_is_not_allowed");
		yield return Case(StreamKind.System, StreamAclKind.Admins, SecurityIdentity.UserOne, StatusCode.PermissionDenied,
			"deleting_system_admin_stream_with_existing_user_is_not_allowed");
		yield return Case(StreamKind.System, StreamAclKind.Admins, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_system_admin_stream_with_admin_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.All, SecurityIdentity.Anonymous, StatusCode.OK,
			"deleting_system_all_stream_with_no_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.All, SecurityIdentity.UserOne, StatusCode.OK,
			"deleting_system_all_stream_with_existing_user_is_allowed");
		yield return Case(StreamKind.System, StreamAclKind.All, SecurityIdentity.Admin, StatusCode.OK,
			"deleting_system_all_stream_with_admin_user_is_allowed");
	}

	[Test]
	public async Task delete_of_all_is_never_allowed()
	{
		AssertStatus(StatusCode.PermissionDenied,
			await ExecuteOperation(SecurityOperation.Tombstone, SystemStreams.AllStream, SecurityIdentity.Anonymous));
		AssertStatus(StatusCode.PermissionDenied,
			await ExecuteOperation(SecurityOperation.Tombstone, SystemStreams.AllStream, SecurityIdentity.UserOne));
		AssertStatus(StatusCode.PermissionDenied,
			await ExecuteOperation(SecurityOperation.Tombstone, SystemStreams.AllStream, SecurityIdentity.Admin));
	}

	[TestCaseSource(nameof(DeleteCases))]
	public async Task deleting_stream_obeys_delete_acl(
		StreamKind streamKind,
		StreamAclKind acl,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		var streamName = await CreateStreamWithDeleteAcl(streamKind, acl);
		AssertStatus(expectedStatus,
			await ExecuteOperation(SecurityOperation.Tombstone, streamName, identity));
	}

	private async Task<string> CreateStreamWithDeleteAcl(StreamKind streamKind, StreamAclKind acl)
	{
		var prefix = streamKind == StreamKind.System ? "$" : string.Empty;
		var streamName = $"{prefix}grpc-delete-security-{Guid.NewGuid():N}";
		var metadata = acl switch
		{
			StreamAclKind.None => "{}",
			StreamAclKind.UserOne => DeleteAclJson(UserOneName),
			StreamAclKind.Admins => DeleteAclJson(SystemRoles.Admins),
			StreamAclKind.All => DeleteAclJson(SystemRoles.All),
			_ => throw new ArgumentOutOfRangeException(nameof(acl), acl, null)
		};
		await SetStreamAcl(streamName, metadata);
		return streamName;
	}

	private static TestCaseData Case(
		StreamKind streamKind,
		StreamAclKind acl,
		SecurityIdentity identity,
		StatusCode expectedStatus,
		string testName) =>
		new TestCaseData(streamKind, acl, identity, expectedStatus).SetName(testName);

	private static string DeleteAclJson(string role) => $"{{\"$acl\":{{\"$d\":\"{role}\"}}}}";
}
