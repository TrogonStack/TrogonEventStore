using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EventStore.Common.Options;
using EventStore.Common.Utils;
using EventStore.Core;
using EventStore.Core.Data;
using EventStore.Core.Services;
using EventStore.Core.Util;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ConfigurationBrowserService(
	ClusterVNodeHostedService hostedService,
	IHttpContextAccessor httpContextAccessor) {
	public ConfigurationPage Read() {
		var options = hostedService.Options;
		var features = new[] {
			new ConfigurationFeature("Projections", options.Projection.RunProjections != ProjectionType.None || options.DevMode.Dev),
			new ConfigurationFeature("User management", options.Auth.AuthenticationType == Opts.AuthenticationTypeDefault && !options.Application.Insecure)
		};

		var subsystems = hostedService.EnabledNodeSubsystems
			.Select(x => new ConfigurationSubsystem(x.ToString()))
			.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var canInspectRuntimeDetails = options.Application.Insecure || CanInspectRuntimeDetails(httpContextAccessor.HttpContext?.User);
		var loadedOptions = canInspectRuntimeDetails
			? options.LoadedOptions.Values
				.OrderBy(x => x.Metadata.SectionMetadata.Sequence)
				.ThenBy(x => x.Metadata.Sequence)
				.Select(ConfigurationOption.From)
				.ToArray()
			: Array.Empty<ConfigurationOption>();

		return new ConfigurationPage(
			VersionInfo.Version,
			canInspectRuntimeDetails ? new IPEndPoint(options.Interface.NodeIp, options.Interface.NodePort).ToString() : "",
			canInspectRuntimeDetails ? options.Database.Db : "",
			canInspectRuntimeDetails ? options.Database.DbLogFormat.ToString() : "",
			canInspectRuntimeDetails ? options.Auth.AuthenticationType : "",
			canInspectRuntimeDetails ? options.Auth.AuthorizationType : "",
			options.Application.Insecure,
			canInspectRuntimeDetails,
			features,
			subsystems,
			loadedOptions,
			canInspectRuntimeDetails ? "" : "Runtime configuration details are visible to operations and admin users.");
	}

	private static bool CanInspectRuntimeDetails(System.Security.Claims.ClaimsPrincipal user) =>
		user?.LegacyRoleCheck(SystemRoles.Operations) == true ||
		user?.LegacyRoleCheck(SystemRoles.Admins) == true;
}

public sealed record ConfigurationPage(
	string Version,
	string NodeEndpoint,
	string DatabasePath,
	string DatabaseLogFormat,
	string AuthenticationType,
	string AuthorizationType,
	bool IsInsecure,
	bool CanInspectRuntimeDetails,
	IReadOnlyList<ConfigurationFeature> Features,
	IReadOnlyList<ConfigurationSubsystem> Subsystems,
	IReadOnlyList<ConfigurationOption> LoadedOptions,
	string OptionsMessage) {
	public string SecurityMode => IsInsecure ? "Insecure" : "Secure";
	public string SecurityTone => IsInsecure ? "warn" : "good";
	public bool HasSubsystems => Subsystems.Count > 0;
	public bool HasLoadedOptions => LoadedOptions.Count > 0;
}

public sealed record ConfigurationFeature(string Name, bool Enabled) {
	public string Status => Enabled ? "Enabled" : "Disabled";
	public string Tone => Enabled ? "good" : "muted";
}

public sealed record ConfigurationSubsystem(string Name);

public sealed record ConfigurationOption(
	string Group,
	string Name,
	string Value,
	string Source,
	string Description,
	string DeprecationMessage,
	bool IsDefault) {
	public string SourceTone => IsDefault ? "muted" : "good";

	public static ConfigurationOption From(LoadedOption option) =>
		new(
			GroupLabel(option.Metadata.SectionMetadata),
			option.Metadata.Name,
			option.DisplayValue,
			option.SourceDisplayName,
			option.Metadata.Description,
			option.Metadata.DeprecationMessage ?? "",
			option.IsDefault);

	private static string GroupLabel(SectionMetadata section) {
		var name = section.SectionName;
		return name.EndsWith("Options", StringComparison.Ordinal)
			? name[..^"Options".Length]
			: name;
	}
}
