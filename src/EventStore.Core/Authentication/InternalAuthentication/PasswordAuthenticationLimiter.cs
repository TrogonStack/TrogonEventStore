using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.RateLimiting;
using EventStore.Common.Exceptions;
using EventStore.Core.Diagnostics;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Authentication.InternalAuthentication;

internal sealed class PasswordAuthenticationLimiter : IDisposable
{
	readonly ConcurrencyLimiter _concurrency;
	readonly TokenBucketRateLimiter _attempts;
	readonly Meter _meter = new(TelemetryMeterInstrumentation.CoreName, TelemetryMeterInstrumentation.ScopeVersion);
	readonly Counter<long> _admitted;
	readonly Counter<long> _rejected;
	readonly UpDownCounter<long> _active;

	public PasswordAuthenticationLimiter(ClusterVNodeOptions.PasswordAuthenticationOptions options)
	{
		if (options.MaxConcurrentAttempts <= 0 || options.AttemptsPerSecond <= 0 || options.BurstSize <= 0)
			throw new InvalidConfigurationException("Auth:Password authentication limits must be positive.");
		if (options.BurstSize < options.AttemptsPerSecond)
			throw new InvalidConfigurationException("Auth:Password:BurstSize must be greater than or equal to Auth:Password:AttemptsPerSecond.");
		_concurrency = new(new ConcurrencyLimiterOptions { PermitLimit = options.MaxConcurrentAttempts, QueueLimit = 0 });
		_attempts = new(new TokenBucketRateLimiterOptions
		{
			TokenLimit = options.BurstSize,
			TokensPerPeriod = options.AttemptsPerSecond,
			ReplenishmentPeriod = TimeSpan.FromSeconds(1),
			AutoReplenishment = false,
			QueueLimit = 0
		});
		var admitted = MetricDefinitions.TrogonEventstoreAuthenticationPasswordAdmitted;
		var rejected = MetricDefinitions.TrogonEventstoreAuthenticationPasswordRejected;
		var active = MetricDefinitions.TrogonEventstoreAuthenticationPasswordActive;
		_admitted = _meter.CreateCounter<long>(admitted.Name, admitted.Unit, admitted.Description);
		_rejected = _meter.CreateCounter<long>(rejected.Name, rejected.Unit, rejected.Description);
		_active = _meter.CreateUpDownCounter<long>(active.Name, active.Unit, active.Description);
	}

	public IDisposable TryAcquire()
	{
		try
		{
			return Acquire();
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
	}

	IDisposable Acquire()
	{
		_attempts.TryReplenish();
		using var attempt = _attempts.AttemptAcquire();
		if (!attempt.IsAcquired)
		{
			_rejected.Add(1, new KeyValuePair<string, object>(TrogonAttributeNames.AuthenticationPasswordRejectionReason, "rate"));
			return null;
		}

		var concurrency = _concurrency.AttemptAcquire();
		if (!concurrency.IsAcquired)
		{
			concurrency.Dispose();
			_rejected.Add(1, new KeyValuePair<string, object>(TrogonAttributeNames.AuthenticationPasswordRejectionReason, "concurrency"));
			return null;
		}

		_admitted.Add(1);
		_active.Add(1);
		return new Lease(concurrency, _active);
	}

	public void Dispose()
	{
		_attempts.Dispose();
		_concurrency.Dispose();
		_meter.Dispose();
	}

	sealed class Lease(RateLimitLease lease, UpDownCounter<long> active) : IDisposable
	{
		RateLimitLease _lease = lease;
		public void Dispose()
		{
			var acquired = Interlocked.Exchange(ref _lease, null);
			if (acquired is null)
				return;
			active.Add(-1);
			acquired.Dispose();
		}
	}
}
