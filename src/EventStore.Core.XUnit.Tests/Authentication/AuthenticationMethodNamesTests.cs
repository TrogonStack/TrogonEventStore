using EventStore.Core.Authentication;
using FluentAssertions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Authentication;

public class AuthenticationMethodNamesTests
{
	[Fact]
	public void defaults_to_password_method()
	{
		AuthenticationMethodNames.FromOptions(new()).Should().Equal(AuthenticationMethodNames.Password);
	}

	[Fact]
	public void maps_legacy_internal_to_password()
	{
		AuthenticationMethodNames.FromOptions(new() { AuthenticationType = "internal" })
			.Should()
			.Equal(AuthenticationMethodNames.Password);
	}

	[Fact]
	public void normalizes_multiple_methods()
	{
		AuthenticationMethodNames.FromOptions(new() { Methods = ["Password", "OAuth", "oauth"] })
			.Should()
			.Equal(AuthenticationMethodNames.Password, AuthenticationMethodNames.OAuth);
	}

	[Fact]
	public void methods_override_legacy_authentication_type()
	{
		AuthenticationMethodNames.FromOptions(new() { AuthenticationType = "ldaps", Methods = ["Password", "OAuth"] })
			.Should()
			.Equal(AuthenticationMethodNames.Password, AuthenticationMethodNames.OAuth);
	}

	[Fact]
	public void keeps_legacy_authentication_type_when_methods_are_not_configured()
	{
		AuthenticationMethodNames.FromOptions(new() { AuthenticationType = "ldaps" })
			.Should()
			.Equal("ldaps");
	}
}
