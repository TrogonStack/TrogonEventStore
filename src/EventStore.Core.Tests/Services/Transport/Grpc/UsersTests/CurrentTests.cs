using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class CurrentTests {
	private static readonly (string userName, string password) OpsCredentials = ("ops", "changeit");

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_current_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private CurrentResp _current;

		protected override Task Given() {
			_client = new UsersClient(Channel);
			return Task.CompletedTask;
		}

		protected override async Task When() {
			_current = await _client.CurrentAsync(new CurrentReq(), GetCallOptions(AdminCredentials));
		}

		[Test]
		public void returns_the_authenticated_user_details() {
			Assert.AreEqual("admin", _current.UserDetails.LoginName);
			Assert.AreEqual("Event Store Administrator", _current.UserDetails.FullName);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_current_user_as_ops<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private CurrentResp _current;

		protected override Task Given() {
			_client = new UsersClient(Channel);
			return Task.CompletedTask;
		}

		protected override async Task When() {
			_current = await _client.CurrentAsync(new CurrentReq(), GetCallOptions(OpsCredentials));
		}

		[Test]
		public void returns_the_authenticated_user_details() {
			Assert.AreEqual("ops", _current.UserDetails.LoginName);
			Assert.AreEqual("Event Store Operations", _current.UserDetails.FullName);
			CollectionAssert.Contains(_current.UserDetails.Groups, "$ops");
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_current_user_without_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override Task Given() {
			_client = new UsersClient(Channel);
			return Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.CurrentAsync(new CurrentReq(), GetCallOptions());
			}
			catch (RpcException ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsNotNull(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, _exception.Status.StatusCode);
		}
	}
}
