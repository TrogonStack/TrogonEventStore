using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class transactional_write_stream_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	public static IEnumerable<TestCaseData> BatchAppendCases()
	{
		yield return Case(StreamAclKind.UserOne, SecurityIdentity.Invalid, StatusCode.Unauthenticated,
			"batch_append_with_not_existing_credentials_is_not_authenticated");
		yield return Case(StreamAclKind.UserOne, SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"batch_append_to_stream_with_no_credentials_is_denied");
		yield return Case(StreamAclKind.UserOne, SecurityIdentity.UserTwo, StatusCode.PermissionDenied,
			"batch_append_to_stream_with_not_authorized_user_credentials_is_denied");
		yield return Case(StreamAclKind.UserOne, SecurityIdentity.UserOne, StatusCode.OK,
			"batch_append_to_stream_with_authorized_user_credentials_succeeds");
		yield return Case(StreamAclKind.UserOne, SecurityIdentity.Admin, StatusCode.OK,
			"batch_append_to_stream_with_admin_user_credentials_succeeds");

		yield return Case(StreamAclKind.None, SecurityIdentity.Anonymous, StatusCode.OK,
			"batch_append_to_no_acl_stream_succeeds_when_no_credentials_are_passed");
		yield return Case(StreamAclKind.None, SecurityIdentity.Invalid, StatusCode.Unauthenticated,
			"batch_append_to_no_acl_stream_is_not_authenticated_when_not_existing_credentials_are_passed");
		yield return Case(StreamAclKind.None, SecurityIdentity.UserOne, StatusCode.OK,
			"batch_append_to_no_acl_stream_succeeds_for_user_one");
		yield return Case(StreamAclKind.None, SecurityIdentity.UserTwo, StatusCode.OK,
			"batch_append_to_no_acl_stream_succeeds_for_user_two");
		yield return Case(StreamAclKind.None, SecurityIdentity.Admin, StatusCode.OK,
			"batch_append_to_no_acl_stream_succeeds_for_admin");

		yield return Case(StreamAclKind.All, SecurityIdentity.Anonymous, StatusCode.OK,
			"batch_append_to_all_access_normal_stream_succeeds_when_no_credentials_are_passed");
		yield return Case(StreamAclKind.All, SecurityIdentity.Invalid, StatusCode.Unauthenticated,
			"batch_append_to_all_access_normal_stream_is_not_authenticated_when_not_existing_credentials_are_passed");
		yield return Case(StreamAclKind.All, SecurityIdentity.UserOne, StatusCode.OK,
			"batch_append_to_all_access_normal_stream_succeeds_for_user_one");
		yield return Case(StreamAclKind.All, SecurityIdentity.UserTwo, StatusCode.OK,
			"batch_append_to_all_access_normal_stream_succeeds_for_user_two");
		yield return Case(StreamAclKind.All, SecurityIdentity.Admin, StatusCode.OK,
			"batch_append_to_all_access_normal_stream_succeeds_for_admin");
	}

	[Test]
	public void public_grpc_streams_service_has_no_explicit_transaction_lifecycle_rpcs()
	{
		var methodNames = EventStore.Client.Streams.Streams.Descriptor.Methods
			.Select(method => method.Name)
			.ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(methodNames, Does.Contain("BatchAppend"));
			Assert.That(methodNames.Where(name => name.Contains("transaction", StringComparison.OrdinalIgnoreCase)),
				Is.Empty);
		});
	}

	[TestCaseSource(nameof(BatchAppendCases))]
	public async Task enforces_multi_event_batch_append_security(
		StreamAclKind acl,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		var streamName = await CreateStreamWithAcl(StreamKind.User, acl, seed: false);
		AssertStatus(expectedStatus, await ExecuteBatchAppend(streamName, identity));
	}

	private static TestCaseData Case(
		StreamAclKind acl,
		SecurityIdentity identity,
		StatusCode status,
		string name) =>
		new TestCaseData(acl, identity, status).SetName(name);
}
