using System.Collections.Generic;
using System.Threading.Tasks;
using EventStore.Core.Services;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class subscribe_to_all_security<TLogFormat, TStreamId> : AuthenticationTestBase<TLogFormat, TStreamId>
{
	public static IEnumerable<TestCaseData> Cases()
	{
		yield return Case(SecurityIdentity.Invalid, StatusCode.Unauthenticated,
			"subscribing_to_all_with_not_existing_credentials_is_not_authenticated");
		yield return Case(SecurityIdentity.Anonymous, StatusCode.PermissionDenied,
			"subscribing_to_all_with_no_credentials_is_denied");
		yield return Case(SecurityIdentity.UserTwo, StatusCode.PermissionDenied,
			"subscribing_to_all_with_not_authorized_user_credentials_is_denied");
		yield return Case(SecurityIdentity.UserOne, StatusCode.OK,
			"subscribing_to_all_with_authorized_user_credentials_succeeds");
		yield return Case(SecurityIdentity.Admin, StatusCode.OK,
			"subscribing_to_all_with_admin_user_credentials_succeeds");
	}

	[TestCaseSource(nameof(Cases))]
	public async Task enforces_subscribe_to_all_security(SecurityIdentity identity, StatusCode expectedStatus)
	{
		await SetStreamAcl(SystemStreams.AllStream, StreamAclKind.UserOne);
		AssertStatus(expectedStatus, await ExecuteAllOperation(true, identity));
	}

	private static TestCaseData Case(SecurityIdentity identity, StatusCode status, string name) =>
		new TestCaseData(identity, status).SetName(name);
}
