#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Exceptions;
using EventStore.Plugins.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace EventStore.Core.Authentication.OAuth;

public class OAuthAuthenticationProvider : AuthenticationProviderBase
{
	private static readonly ILogger Log = Serilog.Log.ForContext<OAuthAuthenticationProvider>();

	private readonly ClusterVNodeOptions.OAuthOptions _options;
	private readonly bool _logFailedAuthenticationAttempts;
	private readonly OAuthTokenValidator _tokenValidator;

	public OAuthAuthenticationProvider(
		ClusterVNodeOptions.OAuthOptions options,
		bool logFailedAuthenticationAttempts,
		Func<CancellationToken, ValueTask<TokenValidationParameters>>? validationParametersFactory = null)
		: base(name: "oauth", diagnosticsName: "OAuthAuthentication")
	{
		_options = options;
		_logFailedAuthenticationAttempts = logFailedAuthenticationAttempts;
		_tokenValidator = new OAuthTokenValidator(options, validationParametersFactory);
	}

	public override void Authenticate(AuthenticationRequest authenticationRequest) =>
		_ = AuthenticateAsync(authenticationRequest);

	public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Bearer"];

	public override IEnumerable<KeyValuePair<string, string>> GetPublicProperties()
	{
		if (!string.IsNullOrWhiteSpace(_options.Issuer))
		{
			yield return new("oauth_issuer", _options.Issuer);
		}

		if (_options.Audiences.Length > 0)
		{
			yield return new("oauth_audiences", string.Join(",", _options.Audiences));
		}

		if (HasBrowserFlow(_options))
		{
			yield return new("authorization_endpoint", BrowserAuthorizationEndpoint(_options));
			yield return new("client_id", _options.ClientId!);
			yield return new("code_challenge_uri", _options.CodeChallengePath);
			yield return new("redirect_uri", _options.RedirectPath);
			yield return new("response_type", "code");
			yield return new("scope", string.Join(" ", _options.Scopes));
		}
	}

	private async Task AuthenticateAsync(AuthenticationRequest authenticationRequest)
	{
		try
		{
			var token = authenticationRequest.GetToken("jwt");
			if (string.IsNullOrWhiteSpace(token))
			{
				authenticationRequest.Unauthorized();
				return;
			}

			var result = await _tokenValidator.ValidateTokenAsync(token, CancellationToken.None);
			if (!result.IsValid)
			{
				if (_logFailedAuthenticationAttempts)
				{
					Log.Warning("Authentication Failed for {Id}: {Reason}", authenticationRequest.Id, result.Exception?.Message ?? "Invalid OAuth token.");
				}

				authenticationRequest.Unauthorized();
				return;
			}

			authenticationRequest.Authenticated(CreatePrincipal(result.ClaimsIdentity));
		}
		catch (Exception ex)
		{
			Log.Error(ex, "OAuth authentication failed unexpectedly.");
			authenticationRequest.Error();
		}
	}

	private ClaimsPrincipal CreatePrincipal(ClaimsIdentity claimsIdentity)
	{
		var claims = claimsIdentity.Claims.ToList();
		var name = claimsIdentity.FindFirst(_options.NameClaimType)?.Value;
		if (!string.IsNullOrWhiteSpace(name) && claims.All(claim => claim.Type != ClaimTypes.Name))
		{
			claims.Add(new Claim(ClaimTypes.Name, name));
		}

		foreach (var role in ExpandRoleClaims(claimsIdentity))
		{
			if (claims.All(claim => claim.Type != ClaimTypes.Role || claim.Value != role))
			{
				claims.Add(new Claim(ClaimTypes.Role, role));
			}
		}

		return new(new ClaimsIdentity(claims, "OAuth", ClaimTypes.Name, ClaimTypes.Role));
	}

	private IEnumerable<string> ExpandRoleClaims(ClaimsIdentity claimsIdentity)
	{
		foreach (var claim in claimsIdentity.FindAll(_options.RoleClaimType))
		{
			if (claim.Value.StartsWith("[", StringComparison.Ordinal))
			{
				foreach (var role in ParseJsonArrayClaim(claim.Value))
				{
					yield return role;
				}

				continue;
			}

			yield return claim.Value;
		}
	}

	private static IEnumerable<string> ParseJsonArrayClaim(string value)
	{
		string[]? values;
		try
		{
			values = JsonSerializer.Deserialize<string[]>(value);
		}
		catch (JsonException)
		{
			yield break;
		}

		if (values is null)
		{
			yield break;
		}

		foreach (var item in values.Where(item => !string.IsNullOrWhiteSpace(item)))
		{
			yield return item;
		}
	}

	private static bool HasBrowserFlow(ClusterVNodeOptions.OAuthOptions options) =>
		!string.IsNullOrWhiteSpace(options.ClientId) &&
		!string.IsNullOrWhiteSpace(options.AuthorizationEndpoint) &&
		!string.IsNullOrWhiteSpace(options.TokenEndpoint) &&
		options.Scopes.Any(scope => !string.IsNullOrWhiteSpace(scope));

	private static string BrowserAuthorizationEndpoint(ClusterVNodeOptions.OAuthOptions options) =>
		options.AuthorizationEndpoint!;
}

public sealed class OAuthTokenValidator
{
	private readonly JsonWebTokenHandler _tokenHandler = new() { MapInboundClaims = false };
	private readonly Func<CancellationToken, ValueTask<TokenValidationParameters>> _validationParametersFactory;

	public OAuthTokenValidator(
		ClusterVNodeOptions.OAuthOptions options,
		Func<CancellationToken, ValueTask<TokenValidationParameters>>? validationParametersFactory = null) =>
		_validationParametersFactory = validationParametersFactory ?? CreateValidationParametersFactory(options);

	public async ValueTask<TokenValidationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken)
	{
		var validationParameters = await _validationParametersFactory(cancellationToken);
		return await _tokenHandler.ValidateTokenAsync(token, validationParameters);
	}

	private static Func<CancellationToken, ValueTask<TokenValidationParameters>> CreateValidationParametersFactory(
		ClusterVNodeOptions.OAuthOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Issuer))
		{
			throw new InvalidConfigurationException("OAuth authentication requires Auth:OAuth:Issuer.");
		}

		if (options.Audiences.Length == 0)
		{
			throw new InvalidConfigurationException("OAuth authentication requires at least one Auth:OAuth:Audiences value.");
		}

		var metadataAddress = string.IsNullOrWhiteSpace(options.MetadataAddress)
			? $"{options.Issuer.TrimEnd('/')}/.well-known/openid-configuration"
			: options.MetadataAddress;

		var retriever = new HttpDocumentRetriever { RequireHttps = options.RequireHttpsMetadata };
		var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
			metadataAddress,
			new OpenIdConnectConfigurationRetriever(),
			retriever);

		return async cancellationToken =>
		{
			var configuration = await configurationManager.GetConfigurationAsync(cancellationToken);
			return new TokenValidationParameters
			{
				ValidateIssuer = true,
				ValidIssuer = configuration.Issuer ?? options.Issuer,
				ValidateAudience = true,
				ValidAudiences = options.Audiences,
				ValidateIssuerSigningKey = true,
				IssuerSigningKeys = configuration.SigningKeys,
				ValidateLifetime = true,
				ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
				NameClaimType = options.NameClaimType,
				RoleClaimType = options.RoleClaimType
			};
		};
	}
}
