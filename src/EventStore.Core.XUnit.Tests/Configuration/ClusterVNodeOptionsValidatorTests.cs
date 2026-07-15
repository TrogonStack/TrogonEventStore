using System;
using EventStore.Common.Exceptions;
using EventStore.Core.Authentication;
using EventStore.Core.Services;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Configuration;

// Some other tests are in ClusterNodeOptionsTests/when_building
public class ClusterVNodeOptionsValidatorTests
{
	[Theory]
	[InlineData(false, false, true)]
	[InlineData(false, true, true)]
	[InlineData(true, false, false)]
	[InlineData(true, true, true)]
	public void archiver_requires_read_only_replica(bool archiver, bool readOnlyReplica, bool expectedValid)
	{
		// given
		var options = new ClusterVNodeOptions
		{
			Cluster = new()
			{
				Archiver = archiver,
				ClusterSize = 3,
				ReadOnlyReplica = readOnlyReplica,
			}
		};

		// when
		void When()
		{
			ClusterVNodeOptionsValidator.Validate(options);
		}

		// then
		if (expectedValid)
		{
			When();
		}
		else
		{
			Assert.Throws<InvalidConfigurationException>(When);
		}
	}

	[Fact]
	public void max_concurrent_read_requests_cannot_be_negative()
	{
		var options = new ClusterVNodeOptions
		{
			Database = new()
			{
				MaxConcurrentReadRequests = -1,
			}
		};

		Assert.Throws<ArgumentOutOfRangeException>(() =>
		{
			ClusterVNodeOptionsValidator.Validate(options);
		});
	}

	[Fact]
	public void archiver_not_compatible_with_unsafe_ignore_hard_delete()
	{
		var options = new ClusterVNodeOptions
		{
			Cluster = new()
			{
				Archiver = true,
				ClusterSize = 3,
				ReadOnlyReplica = true,
			},
			Database = new()
			{
				UnsafeIgnoreHardDelete = true,
			}
		};

		Assert.Throws<InvalidConfigurationException>(() =>
		{
			ClusterVNodeOptionsValidator.Validate(options);
		});
	}

	[Fact]
	public void startup_allows_default_user_passwords_when_oauth_needs_certificate_users()
	{
		var options = new ClusterVNodeOptions
		{
			Auth = new()
			{
				Methods = [AuthenticationMethodNames.OAuth]
			},
			DefaultUser = new()
			{
				DefaultAdminPassword = "admin-password",
				DefaultOpsPassword = "ops-password"
			}
		};

		Assert.True(ClusterVNodeOptionsValidator.ValidateForStartup(options));
	}

	[Fact]
	public void startup_rejects_default_user_passwords_when_builtin_user_store_is_not_used()
	{
		var options = new ClusterVNodeOptions
		{
			Auth = new()
			{
				Methods = ["external"]
			},
			DefaultUser = new()
			{
				DefaultAdminPassword = "admin-password",
				DefaultOpsPassword = "ops-password"
			}
		};

		Assert.False(ClusterVNodeOptionsValidator.ValidateForStartup(options));
	}

	[Fact]
	public void startup_rejects_default_user_passwords_when_auth_is_disabled()
	{
		var options = new ClusterVNodeOptions
		{
			Application = new()
			{
				Insecure = true
			},
			Auth = new()
			{
				Methods = [AuthenticationMethodNames.OAuth]
			},
			DefaultUser = new()
			{
				DefaultAdminPassword = "admin-password",
				DefaultOpsPassword = SystemUsers.DefaultOpsPassword
			}
		};

		Assert.False(ClusterVNodeOptionsValidator.ValidateForStartup(options));
	}
}
