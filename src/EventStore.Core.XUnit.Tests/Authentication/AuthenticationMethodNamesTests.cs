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
}
