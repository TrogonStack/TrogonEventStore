using System;
using System.Threading.Tasks;
using EventStore.ClientAPI.Exceptions;
using EventStore.ClientAPI.SystemData;
using EventStore.ClientAPI.Transport.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.ClientAPI.UserManagement;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class reset_password<TLogFormat, TStreamId> : TestWithUser<TLogFormat, TStreamId>
{
	[Test]
	public async Task null_user_name_throws()
	{
		await AssertEx.ThrowsAsync<ArgumentNullException>(() =>
			_manager.ResetPasswordAsync(null, "foo", new UserCredentials("admin", "changeit")));
	}

	[Test]
	public async Task empty_user_name_throws()
	{
		await AssertEx.ThrowsAsync<ArgumentNullException>(() =>
			_manager.ResetPasswordAsync("", "foo", new UserCredentials("admin", "changeit")));
	}

	[Test]
	public async Task empty_password_throws()
	{
		await AssertEx.ThrowsAsync<ArgumentNullException>(() =>
			_manager.ResetPasswordAsync(_username, "", new UserCredentials("admin", "changeit")));
	}

	[Test]
	public async Task null_password_throws()
	{
		await AssertEx.ThrowsAsync<ArgumentNullException>(() =>
			_manager.ResetPasswordAsync(_username, null, new UserCredentials("admin", "changeit")));
	}

	[Test]
	public async Task can_reset_password()
	{
		await _manager.ResetPasswordAsync(_username, "foo", new UserCredentials("admin", "changeit"));
		var ex = await AssertEx.ThrowsAsync<AggregateException>(
			() => _manager.GetCurrentUserAsync(new UserCredentials(_username, "password")));
		Assert.AreEqual(HttpStatusCode.Unauthorized,
			((UserCommandFailedException)ex.InnerException).HttpStatusCode);

		var current = await _manager.GetCurrentUserAsync(new UserCredentials(_username, "foo"));
		Assert.AreEqual(_username, current.LoginName);
	}
}
