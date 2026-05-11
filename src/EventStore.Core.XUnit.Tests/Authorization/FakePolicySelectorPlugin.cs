#nullable enable
using System;
using System.Threading.Tasks;
using DotNext.Threading;
using EventStore.Core.Authorization;
using EventStore.Core.Authorization.AuthorizationPolicies;
using EventStore.Core.Bus;

namespace EventStore.Core.XUnit.Tests.Authorization;

public class FakePolicySelectorPlugin(string name, bool canBeEnabled) : IPolicySelectorFactory
{
	public readonly AsyncManualResetEvent OnEnabled = new(false);
	public readonly AsyncManualResetEvent OnDisabled = new(false);
	public string CommandLineName => name;

	public IPolicySelector Create(IPublisher publisher) => new FakePolicySelector(name);

	public Task<bool> Enable()
	{
		OnEnabled.Set();
		return Task.FromResult(canBeEnabled);
	}

	public Task Disable()
	{
		OnDisabled.Set();
		return Task.CompletedTask;
	}
}

public class FakePolicySelector(string name) : IPolicySelector
{
	public ReadOnlyPolicy Select() => new Policy(name, 1, DateTimeOffset.MaxValue).AsReadOnly();
}
