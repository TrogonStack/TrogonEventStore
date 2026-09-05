using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Exceptions;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Messages;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Tests.Helpers;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Tests.Authentication;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class PasswordAuthenticationAdmissionTests<TLogFormat, TStreamId> :
	with_internal_authentication_provider<TLogFormat, TStreamId>
{
	protected override void Given() => ExistingEvent("$user-user", "$UserCreated", null,
		"{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:['reader']}");

	[SetUp]
	public void SetUp() => SetUpProvider();

	[TestCase(0, 1, 1)]
	[TestCase(-1, 1, 1)]
	[TestCase(1, 0, 1)]
	[TestCase(1, -1, 1)]
	[TestCase(1, 1, 0)]
	[TestCase(1, 1, -1)]
	public void invalid_limits_fail_before_serving_password_requests(int concurrent, int rate, int burst)
	{
		Assert.Throws<InvalidConfigurationException>(() => new InternalAuthenticationProvider(
			_bus, _ioDispatcher, new StubPasswordHashAlgorithm(), 1000, false,
			DefaultData.DefaultUserOptions, new()
			{
				MaxConcurrentAttempts = concurrent,
				AttemptsPerSecond = rate,
				BurstSize = burst
			}));
	}

	[Test]
	public void admission_metrics_report_bounded_rejection_reasons_and_active_work()
	{
		var admitted = MetricDefinitions.TrogonEventstoreAuthenticationPasswordAdmitted.Name;
		var rejected = MetricDefinitions.TrogonEventstoreAuthenticationPasswordRejected.Name;
		var active = MetricDefinitions.TrogonEventstoreAuthenticationPasswordActive.Name;
		var values = new Dictionary<string, long> { [admitted] = 0, [rejected] = 0, [active] = 0 };
		var attributes = new List<KeyValuePair<string, object>>();
		using var listener = new MeterListener();
		listener.InstrumentPublished = (instrument, owner) =>
		{
			if (values.ContainsKey(instrument.Name))
				owner.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
		{
			values[instrument.Name] += measurement;
			foreach (var tag in tags)
				attributes.Add(tag);
		});
		listener.Start();
		ReadsBackwardQueuesUp();
		var request = new TestAuthenticationRequest("user", "password", () => { }, _ => { }, () => { }, () => { });
		for (var index = 0; index < 5; index++)
			_internalAuthenticationProvider.AuthenticateSession(request);
		Assert.That(values[admitted], Is.EqualTo(4));
		Assert.That(values[rejected], Is.EqualTo(1));
		Assert.That(values[active], Is.EqualTo(4));
		CompleteOneReadBackwards();
		Assert.That(values[active], Is.EqualTo(3));
		Assert.That(attributes, Is.EqualTo(new[]
		{
			new KeyValuePair<string, object>(TrogonAttributeNames.AuthenticationPasswordRejectionReason, "concurrency")
		}));
	}

	[Test]
	public void api_and_browser_password_authentication_share_bounded_pending_reads()
	{
		ReadsBackwardQueuesUp();
		_consumer.HandledMessages.Clear();
		var rejected = 0;
		for (var index = 0; index < 5; index++)
		{
			var request = new TestAuthenticationRequest("user", "password", () => { }, _ => { }, () => { }, () => rejected++);
			if (index % 2 == 0)
				_internalAuthenticationProvider.Authenticate(request);
			else
				_internalAuthenticationProvider.AuthenticateSession(request);
		}

		Assert.That(rejected, Is.EqualTo(1));
		Assert.That(_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsBackward>().Count(), Is.EqualTo(4));
		CompleteOneReadBackwards();
		_internalAuthenticationProvider.AuthenticateSession(new TestAuthenticationRequest("user", "password",
			() => { }, _ => { }, () => { }, () => rejected++));
		Assert.That(rejected, Is.EqualTo(1));
		Assert.That(_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsBackward>().Count(), Is.EqualTo(5));
	}

	[Test]
	public void cached_password_verification_cannot_bypass_pending_browser_attempts()
	{
		var authenticated = 0;
		var rejected = 0;
		var request = new TestAuthenticationRequest("user", "password", () => { }, _ => authenticated++, () => { }, () => rejected++);
		_internalAuthenticationProvider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(1));
		ReadsBackwardQueuesUp();
		for (var index = 0; index < 4; index++)
			_internalAuthenticationProvider.AuthenticateSession(request);
		_internalAuthenticationProvider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(1));
		Assert.That(rejected, Is.EqualTo(1));
		CompleteOneReadBackwards();
		_internalAuthenticationProvider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(3));
	}

	[TestCase("missing")]
	[TestCase("disabled")]
	[TestCase("malformed")]
	[TestCase("not-ready")]
	[TestCase("timeout")]
	public void failed_account_reads_release_capacity(string failure)
	{
		switch (failure)
		{
			case "missing":
				NoStream("$user-user");
				break;
			case "disabled":
				ExistingEvent("$user-user", "$UserUpdated", null,
				"{LoginName:'user',Salt:'drowssap',Hash:'password',Groups:[],Disabled:true}");
				break;
			case "malformed":
				ExistingEvent("$user-user", "$UserUpdated", null, "invalid");
				break;
			case "not-ready":
				NotReady();
				break;
			case "timeout":
				AllReadsTimeOut();
				break;
		}
		var outcomes = 0;
		var request = new TestAuthenticationRequest("user", "password", () => outcomes++, _ => outcomes++, () => outcomes++, () => outcomes++);
		_consumer.HandledMessages.Clear();
		for (var index = 0; index < 10; index++)
		{
			_internalAuthenticationProvider.Authenticate(request);
			if (failure == "timeout")
				_consumer.HandledMessages.OfType<TimerMessage.Schedule>().Last().Reply();
		}
		Assert.That(outcomes, Is.EqualTo(10));
		Assert.That(_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsBackward>().Count(), Is.EqualTo(10));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task capacity_is_held_until_password_verification_finishes(bool browser)
	{
		using var hashing = new ControlledHash();
		var provider = new InternalAuthenticationProvider(_bus, _ioDispatcher, hashing, 1000, false,
			DefaultData.DefaultUserOptions, new() { MaxConcurrentAttempts = 1 });
		var authenticated = 0;
		var rejected = 0;
		var request = new TestAuthenticationRequest("user", "password", () => { }, _ => Interlocked.Increment(ref authenticated),
			() => { }, () => Interlocked.Increment(ref rejected));
		provider.Authenticate(request);
		hashing.Block = true;
		var running = Task.Run(() =>
		{
			if (browser)
				provider.AuthenticateSession(request);
			else
				provider.Authenticate(request);
		});
		try
		{
			Assert.That(hashing.Entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
			provider.Authenticate(request);
			Assert.That(rejected, Is.EqualTo(1));
			Assert.That(authenticated, Is.EqualTo(1));
		}
		finally
		{
			hashing.Release.Set();
			await running.WaitAsync(TimeSpan.FromSeconds(5));
		}
		provider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(3));
	}

	[Test]
	public async Task burst_exhaustion_limits_missing_users_and_recovers()
	{
		NoStream("$user-user");
		var provider = new InternalAuthenticationProvider(_bus, _ioDispatcher, new StubPasswordHashAlgorithm(), 1000, false,
			DefaultData.DefaultUserOptions, new() { BurstSize = 1, AttemptsPerSecond = 1 });
		var rejected = 0;
		var unauthorized = 0;
		var request = new TestAuthenticationRequest("user", "password", () => unauthorized++, _ => { }, () => { }, () => rejected++);
		provider.Authenticate(request);
		provider.AuthenticateSession(request);
		Assert.That(unauthorized, Is.EqualTo(1));
		Assert.That(rejected, Is.EqualTo(1));
		await Task.Delay(TimeSpan.FromMilliseconds(1100));
		provider.Authenticate(request);
		Assert.That(unauthorized, Is.EqualTo(2));
	}

	[Test]
	public void timeout_then_late_read_cannot_release_another_attempts_capacity()
	{
		var provider = new InternalAuthenticationProvider(_bus, _ioDispatcher, new StubPasswordHashAlgorithm(), 1000, false,
			DefaultData.DefaultUserOptions, new() { MaxConcurrentAttempts = 1 });
		ReadsBackwardQueuesUp();
		var rejected = 0;
		var authenticated = 0;
		var request = new TestAuthenticationRequest("user", "password", () => { }, _ => authenticated++, () => { }, () => rejected++);
		provider.AuthenticateSession(request);
		var timeout = _consumer.HandledMessages.OfType<TimerMessage.Schedule>().Last();
		timeout.Reply();
		provider.AuthenticateSession(request);
		timeout.Reply();
		CompleteOneReadBackwards();
		provider.AuthenticateSession(request);
		Assert.That(rejected, Is.EqualTo(2));
		Assert.That(authenticated, Is.Zero);
		CompleteOneReadBackwards();
		provider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(2));
	}

	[Test]
	public void password_verifier_exception_releases_capacity()
	{
		using var hashing = new ControlledHash { Throw = true };
		var provider = new InternalAuthenticationProvider(_bus, _ioDispatcher, hashing, 1000, false,
			DefaultData.DefaultUserOptions, new() { MaxConcurrentAttempts = 1 });
		var authenticated = 0;
		var unauthorized = 0;
		var request = new TestAuthenticationRequest("user", "password", () => unauthorized++, _ => authenticated++,
			() => Assert.Fail("Error"), () => Assert.Fail("Capacity was not released"));
		provider.Authenticate(request);
		Assert.That(unauthorized, Is.EqualTo(1));
		hashing.Throw = false;
		provider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(1));
		hashing.Throw = true;
		Assert.Throws<InvalidOperationException>(() => provider.Authenticate(request));
		hashing.Throw = false;
		provider.Authenticate(request);
		Assert.That(authenticated, Is.EqualTo(2));
	}

	[Test]
	public async Task existing_session_validation_does_not_consume_password_attempts()
	{
		var provider = new InternalAuthenticationProvider(_bus, _ioDispatcher, new StubPasswordHashAlgorithm(), 1000, false,
			DefaultData.DefaultUserOptions, new() { BurstSize = 1, AttemptsPerSecond = 1 });
		ClaimsPrincipal principal = null;
		provider.Authenticate(new TestAuthenticationRequest("user", "password", () => { }, value => principal = value, () => { }, () => { }));
		Assert.That(await provider.ValidateSessionAsync(principal, CancellationToken.None), Is.Not.Null);
	}

	[Test]
	public void shutdown_rejects_password_work_without_throwing()
	{
		_bus.Publish(new SystemMessage.BecomeShutdown(Guid.NewGuid()));
		var rejected = 0;
		_internalAuthenticationProvider.Authenticate(new TestAuthenticationRequest("user", "password", () => { }, _ => { }, () => { }, () => rejected++));
		Assert.That(rejected, Is.EqualTo(1));
	}

	[Test]
	public async Task certificate_authentication_bypasses_exhausted_password_budget()
	{
		var provider = new InternalAuthenticationProvider(_bus, _ioDispatcher, new StubPasswordHashAlgorithm(), 1000, false,
			DefaultData.DefaultUserOptions, new() { BurstSize = 1, AttemptsPerSecond = 1 });
		provider.Authenticate(new TestAuthenticationRequest("user", "wrong-password", () => { }, _ => { }, () => { }, () => { }));
		using var rsa = RSA.Create(2048);
		var certificateRequest = new CertificateRequest("CN=user", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
		var request = HttpAuthenticationRequest.CreateWithValidCertificate(new DefaultHttpContext(), "user", certificate);
		provider.Authenticate(request);
		var (status, principal) = await request.AuthenticateAsync();
		Assert.That(status, Is.EqualTo(HttpAuthenticationRequestStatus.Authenticated));
		Assert.That(principal.Identity.Name, Is.EqualTo("user"));
	}

	sealed class ControlledHash : StubPasswordHashAlgorithm, IDisposable
	{
		public bool Block;
		public bool Throw;
		public readonly ManualResetEventSlim Entered = new();
		public readonly ManualResetEventSlim Release = new();
		public override bool Verify(string password, string hash, string salt)
		{
			if (Throw)
				throw new InvalidOperationException();
			if (Block)
			{
				Entered.Set();
				if (!Release.Wait(TimeSpan.FromSeconds(10)))
					throw new TimeoutException();
			}
			return base.Verify(password, hash, salt);
		}
		public void Dispose()
		{
			Entered.Dispose();
			Release.Dispose();
		}
	}
}
