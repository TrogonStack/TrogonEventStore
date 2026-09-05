using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.DelegatedAuthentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Messages;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class InternalSessionAuthenticationTests<TLogFormat, TStreamId> :
	with_internal_authentication_provider<TLogFormat, TStreamId>
{
	protected override void Given() =>
		ExistingEvent("$user-user", "$UserCreated", null,
			"{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['reader']}");

	[SetUp]
	public void SetUp() => SetUpProvider();

	private ClaimsPrincipal SignIn()
	{
		ClaimsPrincipal principal = null;
		_internalAuthenticationProvider.Authenticate(new TestAuthenticationRequest("user", "password",
			() => Assert.Fail("Authentication rejected valid credentials"), value => principal = value,
			() => Assert.Fail("Authentication failed"), () => Assert.Fail("Authentication not ready")));
		return principal;
	}

	private ClaimsPrincipal SignInSession(string password = "password", ISessionAuthenticationProvider provider = null)
	{
		ClaimsPrincipal principal = null;
		(provider ?? _internalAuthenticationProvider).AuthenticateSession(new TestAuthenticationRequest("user", password,
			() => { }, value => principal = value,
			() => Assert.Fail("Authentication failed"), () => Assert.Fail("Authentication not ready")));
		return principal;
	}

	[Test]
	public void successful_password_authentication_provides_account_version_for_session_validation()
	{
		var principal = SignIn();
		Assert.That(principal.FindFirst("es:session-security-stamp")?.Value, Is.Not.Null.And.Not.Empty);
	}

	[Test]
	public async Task session_validation_reads_current_account_and_rebuilds_roles()
	{
		var principal = SignIn();
		((ClaimsIdentity)principal.Identity).AddClaim(new Claim(ClaimTypes.Role, "$admins"));
		_consumer.HandledMessages.Clear();

		var validated = await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None);

		Assert.That(validated.Identity.Name, Is.EqualTo("user"));
		Assert.That(validated.IsInRole("reader"), Is.True);
		Assert.That(validated.IsInRole("$admins"), Is.False);
		Assert.That(_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsBackward>().Count(), Is.EqualTo(1));
	}

	[TestCase("{LoginName:'user',Salt:'drowssap',Hash:'newpassword',Groups:['reader']}")]
	[TestCase("{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['other']}")]
	[TestCase("{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['reader'],Disabled:true}")]
	[TestCase("{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['reader']}")]
	public async Task any_account_update_revokes_session_without_waiting_for_cache_notification(string account)
	{
		var principal = SignIn();
		ExistingEvent("$user-user", "$UserUpdated", null, account);

		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None), Is.Null);
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(SignIn(), CancellationToken.None), Is.Null);
	}

	[Test]
	public async Task fresh_password_sign_in_after_account_update_can_create_session_without_cache_notification()
	{
		var oldPrincipal = SignIn();
		ExistingEvent("$user-user", "$UserUpdated", null,
			"{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['other']}");

		var current = await _internalAuthenticationProvider.ValidateSessionAsync(SignInSession(), CancellationToken.None);

		Assert.That(current, Is.Not.Null);
		Assert.That(current.IsInRole("other"), Is.True);
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(oldPrincipal, CancellationToken.None), Is.Null);
	}

	[TestCase("{LoginName:'user',Salt:'drowssap',Hash:'newpassword',Groups:['reader']}")]
	[TestCase("{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['reader'],Disabled:true}")]
	public void new_session_rejects_cached_password_after_password_change_or_disable(string account)
	{
		SignIn();
		ExistingEvent("$user-user", "$UserUpdated", null, account);

		Assert.That(SignInSession(), Is.Null);
	}

	[Test]
	public async Task new_session_accepts_changed_password_without_cache_notification()
	{
		var oldPrincipal = SignIn();
		ExistingEvent("$user-user", "$UserUpdated", null,
			"{LoginName:'user',Salt:'drowssapwen',Hash:'newpassword',Groups:['reader']}");

		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(SignInSession("newpassword"), CancellationToken.None), Is.Not.Null);
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(oldPrincipal, CancellationToken.None), Is.Null);
	}

	[Test]
	public async Task delegated_composite_session_sign_in_reads_current_account_without_retaining_credentials()
	{
		SignIn();
		ExistingEvent("$user-user", "$UserUpdated", null,
			"{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['other']}");
		var provider = new DelegatedAuthenticationProvider(new CompositeAuthenticationProvider([_internalAuthenticationProvider]));

		var principal = SignInSession(provider: provider);

		Assert.That(principal, Is.Not.Null);
		Assert.That(principal.Identities.OfType<DelegatedClaimsIdentity>(), Is.Empty);
		Assert.That((await provider.ValidateSessionAsync(principal, CancellationToken.None)).IsInRole("other"), Is.True);
	}

	[Test]
	public async Task deleting_account_revokes_session()
	{
		var principal = SignIn();
		DeletedStream("$user-user");
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None), Is.Null);
	}

	[Test]
	public async Task missing_account_revokes_session()
	{
		var principal = SignIn();
		NoStream("$user-user");
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None), Is.Null);
	}

	[Test]
	public async Task unavailable_account_read_fails_closed()
	{
		var principal = SignIn();
		NotReady();
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None), Is.Null);
	}

	[Test]
	public async Task cancellation_of_pending_read_fails_closed()
	{
		var principal = SignIn();
		ReadsBackwardQueuesUp();
		using var cancellation = new CancellationTokenSource();
		var validation = _internalAuthenticationProvider.ValidateSessionAsync(principal, cancellation.Token);
		cancellation.Cancel();
		Assert.That(await validation.WaitAsync(TimeSpan.FromSeconds(1)), Is.Null);
		CompleteOneReadBackwards();
	}

	[Test]
	public async Task unavailable_read_has_bounded_wait_even_without_dispatcher_timeout()
	{
		var principal = SignIn();
		ReadsBackwardQueuesUp();
		var validation = _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None);
		Assert.That(await validation.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null);
		CompleteOneReadBackwards();
	}

	[Test]
	public async Task absent_security_stamp_fails_closed_without_reading()
	{
		var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "test"));
		_consumer.HandledMessages.Clear();
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None), Is.Null);
		Assert.That(_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsBackward>(), Is.Empty);
	}

	[Test]
	public async Task delegated_composite_provider_preserves_session_validation()
	{
		var provider = new DelegatedAuthenticationProvider(new CompositeAuthenticationProvider([_internalAuthenticationProvider]));
		Assert.That(await provider.ValidateSessionAsync(SignIn(), CancellationToken.None), Is.Not.Null);
	}

	[Test]
	public async Task incorrect_stamp_fails_closed()
	{
		var principal = SignIn();
		var identity = (ClaimsIdentity)principal.Identity;
		identity.RemoveClaim(identity.FindFirst(InternalAuthenticationProvider.SessionSecurityStampClaimType));
		identity.AddClaim(new Claim(InternalAuthenticationProvider.SessionSecurityStampClaimType, Guid.NewGuid().ToString("N")));
		Assert.That(await _internalAuthenticationProvider.ValidateSessionAsync(principal, CancellationToken.None), Is.Null);
	}
}
