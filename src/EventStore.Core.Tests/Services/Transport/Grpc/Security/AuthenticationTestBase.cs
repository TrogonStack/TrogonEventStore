using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Streams;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using UsersClient = EventStore.Client.Users.Users.UsersClient;
using UsersCreateReq = EventStore.Client.Users.CreateReq;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Security;

public enum SecurityOperation
{
	ReadEvent,
	ReadForward,
	ReadBackward,
	Subscribe,
	Write,
	Tombstone,
	ReadMetadata,
	WriteMetadata
}

public enum SecurityIdentity
{
	Anonymous,
	Invalid,
	UserOne,
	UserTwo,
	Admin
}

public enum StreamAclKind
{
	None,
	UserOne,
	Admins,
	All
}

public enum StreamKind
{
	User,
	System
}

public readonly record struct StreamSecurityCase(
	StreamKind StreamKind,
	StreamAclKind Acl,
	SecurityIdentity Identity,
	StatusCode ExpectedStatus,
	string TestName);

public abstract class AuthenticationTestBase<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	protected const string UserOneName = "user1";
	protected const string UserTwoName = "user2";
	protected const string AdminName = "adm";
	private const string UserOnePassword = "pa$$1";
	private const string UserTwoPassword = "pa$$2";
	private const string AdminPassword = "admpa$$";
	private readonly SecurityIdentity? _defaultIdentity;

	protected AuthenticationTestBase(SecurityIdentity? defaultIdentity = null)
	{
		_defaultIdentity = defaultIdentity;
	}

	protected static (string userName, string password) UserOneCredentials => (UserOneName, UserOnePassword);
	protected static (string userName, string password) UserTwoCredentials => (UserTwoName, UserTwoPassword);
	protected static (string userName, string password) NamedAdminCredentials => (AdminName, AdminPassword);

	protected override (string userName, string password) DefaultCredentials =>
		_defaultIdentity is { } identity ? CredentialsFor(identity) : default;

	protected override async Task Given()
	{
		await CreateUser(UserOneName, UserOnePassword);
		await CreateUser(UserTwoName, UserTwoPassword);
		await CreateUser(AdminName, AdminPassword, SystemRoles.Admins);
	}

	protected override Task When() => Task.CompletedTask;

	protected CallOptions OptionsFor(SecurityIdentity identity) => identity switch
	{
		SecurityIdentity.Anonymous => GetCallOptions(),
		SecurityIdentity.Invalid => GetCallOptions(("badlogin", "badpass")),
		SecurityIdentity.UserOne => GetCallOptions(UserOneCredentials),
		SecurityIdentity.UserTwo => GetCallOptions(UserTwoCredentials),
		SecurityIdentity.Admin => GetCallOptions(NamedAdminCredentials),
		_ => throw new ArgumentOutOfRangeException(nameof(identity), identity, null)
	};

	protected async Task<string> CreateStreamWithAcl(
		StreamKind streamKind,
		StreamAclKind acl,
		bool seed = true)
	{
		var prefix = streamKind == StreamKind.System ? "$" : string.Empty;
		var streamName = $"{prefix}grpc-security-{Guid.NewGuid():N}";
		if (acl != StreamAclKind.None)
		{
			await SetStreamAcl(streamName, acl);
		}

		if (seed)
		{
			await Append(streamName, OptionsFor(SecurityIdentity.Admin));
		}

		return streamName;
	}

	protected async Task SetStreamAcl(string streamName, StreamAclKind acl) =>
		await SetStreamAcl(streamName, AclJson(acl));

	protected async Task SetStreamAcl(string streamName, string aclJson) =>
		await Append(SystemStreams.MetastreamOf(streamName), OptionsFor(SecurityIdentity.Admin),
			SystemEventTypes.StreamMetadata, aclJson);

	protected async Task SetSystemSettings(string settingsJson) =>
		await Append(SystemStreams.SettingsStream, OptionsFor(SecurityIdentity.Admin), "security-settings", settingsJson);

	protected async Task SetDefaultAcl(StreamKind streamKind, StreamAclKind acl)
	{
		var property = streamKind == StreamKind.System ? "$systemStreamAcl" : "$userStreamAcl";
		await SetSystemSettings($"{{\"{property}\":{AclBodyJson(acl)}}}");
	}

	protected Task<StatusCode> ExecuteOperation(
		SecurityOperation operation,
		string streamName,
		SecurityIdentity identity) => ExecuteOperation(operation, streamName, OptionsFor(identity));

	protected Task<StatusCode> ExecuteOperationWithDefault(
		SecurityOperation operation,
		string streamName) => ExecuteOperation(operation, streamName, GetCallOptions());

	protected async Task<StatusCode> ExecuteAllOperation(
		bool subscribe,
		SecurityIdentity identity,
		bool backwards = false) =>
		await CaptureStatus(() => ReadAll(OptionsFor(identity), subscribe, backwards));

	protected async Task<StatusCode> ExecuteAllOperationWithDefault(
		bool subscribe,
		bool backwards = false) =>
		await CaptureStatus(() => ReadAll(GetCallOptions(), subscribe, backwards));

	protected Task<StatusCode> ExecuteBatchAppend(string streamName, SecurityIdentity identity) =>
		ExecuteBatchAppend(streamName, OptionsFor(identity));

	protected Task<StatusCode> ExecuteBatchAppendWithDefault(string streamName) =>
		ExecuteBatchAppend(streamName, GetCallOptions());

	private async Task<StatusCode> ExecuteBatchAppend(string streamName, CallOptions callOptions)
	{
		try
		{
			using var call = StreamsClient.BatchAppend(callOptions);
			var correlationId = Uuid.NewUuid();
			await call.RequestStream.WriteAsync(new BatchAppendReq
			{
				Options = new BatchAppendReq.Types.Options
				{
					Any = new Google.Protobuf.WellKnownTypes.Empty(),
					StreamIdentifier = StreamIdentifier(streamName)
				},
				CorrelationId = correlationId.ToDto(),
				IsFinal = true,
				ProposedMessages = { ProposedBatchMessage(), ProposedBatchMessage() }
			});
			await call.RequestStream.CompleteAsync();
			if (!await call.ResponseStream.MoveNext())
			{
				return StatusCode.Unknown;
			}

			var response = call.ResponseStream.Current;
			return response.ResultCase == BatchAppendResp.ResultOneofCase.Success
				? StatusCode.OK
				: (StatusCode)response.Error.Code;
		}
		catch (RpcException ex)
		{
			return ex.StatusCode;
		}
	}

	protected async Task AssertAllStreamOperations(
		string streamName,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		foreach (var operation in AllStreamOperations())
		{
			var status = await ExecuteOperation(operation, streamName, identity);
			Assert.AreEqual(expectedStatus, status, operation.ToString());
		}
	}

	protected async Task AssertAllStreamOperations(
		StreamKind streamKind,
		StreamAclKind acl,
		SecurityIdentity identity,
		StatusCode expectedStatus)
	{
		foreach (var operation in AllStreamOperations())
		{
			var streamName = await CreateStreamWithAcl(streamKind, acl);
			var status = await ExecuteOperation(operation, streamName, identity);
			Assert.AreEqual(expectedStatus, status, operation.ToString());
		}
	}

	protected async Task AssertAllStreamOperationsWithDefault(
		string streamName,
		StatusCode expectedStatus)
	{
		foreach (var operation in AllStreamOperations())
		{
			var status = await ExecuteOperationWithDefault(operation, streamName);
			Assert.AreEqual(expectedStatus, status, operation.ToString());
		}
	}

	protected static void AssertStatus(StatusCode expectedStatus, StatusCode actualStatus) =>
		Assert.AreEqual(expectedStatus, actualStatus);

	protected static string AclJson(string role) =>
		$"{{\"$acl\":{AclBodyJson(role)}}}";

	protected static string WriteAclJson(params string[] roles)
	{
		var values = string.Join(',', Array.ConvertAll(roles, role => $"\"{role}\""));
		return $"{{\"$acl\":{{\"$w\":[{values}]}}}}";
	}

	private async Task<StatusCode> ExecuteOperation(
		SecurityOperation operation,
		string streamName,
		CallOptions callOptions) => operation switch
		{
			SecurityOperation.ReadEvent => await CaptureStatus(() => ReadStream(streamName, callOptions, false, false, true)),
			SecurityOperation.ReadForward => await CaptureStatus(() => ReadStream(streamName, callOptions, false, false, false)),
			SecurityOperation.ReadBackward => await CaptureStatus(() => ReadStream(streamName, callOptions, false, true, false)),
			SecurityOperation.Subscribe => await CaptureStatus(() => ReadStream(streamName, callOptions, true, false, false)),
			SecurityOperation.Write => await CaptureStatus(() => Append(streamName, callOptions)),
			SecurityOperation.Tombstone => await CaptureStatus(() => Tombstone(streamName, callOptions)),
			SecurityOperation.ReadMetadata => await CaptureStatus(() =>
				ReadStream(SystemStreams.MetastreamOf(streamName), callOptions, false, true, false)),
			SecurityOperation.WriteMetadata => await CaptureStatus(() => Append(
				SystemStreams.MetastreamOf(streamName), callOptions, SystemEventTypes.StreamMetadata, "{}")),
			_ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
		};

	private async Task CreateUser(string loginName, string password, params string[] groups)
	{
		var users = new UsersClient(Channel);
		var request = new UsersCreateReq
		{
			Options = new UsersCreateReq.Types.Options
			{
				FullName = loginName,
				LoginName = loginName,
				Password = password,
				Groups = { groups }
			}
		};
		await users.CreateAsync(request, GetCallOptions(AdminCredentials));
	}

	private async Task Append(
		string streamName,
		CallOptions callOptions,
		string eventType = "security-event",
		string data = "{}")
	{
		using var call = StreamsClient.Append(callOptions);
		await call.RequestStream.WriteAsync(new AppendReq
		{
			Options = new AppendReq.Types.Options
			{
				Any = new Empty(),
				StreamIdentifier = StreamIdentifier(streamName)
			}
		});
		await call.RequestStream.WriteAsync(new AppendReq
		{
			ProposedMessage = new AppendReq.Types.ProposedMessage
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFromUtf8(data),
				Metadata =
				{
					{ GrpcMetadata.ContentType, GrpcMetadata.ContentTypes.ApplicationJson },
					{ GrpcMetadata.Type, eventType }
				}
			}
		});
		await call.RequestStream.CompleteAsync();
		await call.ResponseAsync;
	}

	private async Task ReadStream(
		string streamName,
		CallOptions callOptions,
		bool subscribe,
		bool backwards,
		bool singleEvent)
	{
		var readBackwards = backwards || singleEvent;
		var streamOptions = new ReadReq.Types.Options.Types.StreamOptions
		{
			StreamIdentifier = StreamIdentifier(streamName)
		};
		if (singleEvent)
		{
			streamOptions.End = new Empty();
		}
		else if (backwards)
		{
			streamOptions.End = new Empty();
		}
		else
		{
			streamOptions.Start = new Empty();
		}

		var options = new ReadReq.Types.Options
		{
			Stream = streamOptions,
			ReadDirection = readBackwards
				? ReadReq.Types.Options.Types.ReadDirection.Backwards
				: ReadReq.Types.Options.Types.ReadDirection.Forwards,
			NoFilter = new Empty(),
			UuidOption = new ReadReq.Types.Options.Types.UUIDOption { Structured = new Empty() }
		};
		if (subscribe)
		{
			options.Subscription = new ReadReq.Types.Options.Types.SubscriptionOptions();
		}
		else
		{
			options.Count = 1;
		}

		using var call = StreamsClient.Read(new ReadReq { Options = options }, callOptions);
		await call.ResponseStream.MoveNext();
	}

	private async Task ReadAll(CallOptions callOptions, bool subscribe, bool backwards)
	{
		var allOptions = new ReadReq.Types.Options.Types.AllOptions();
		if (backwards)
		{
			allOptions.End = new Empty();
		}
		else
		{
			allOptions.Start = new Empty();
		}

		var options = new ReadReq.Types.Options
		{
			All = allOptions,
			ReadDirection = backwards
				? ReadReq.Types.Options.Types.ReadDirection.Backwards
				: ReadReq.Types.Options.Types.ReadDirection.Forwards,
			NoFilter = new Empty(),
			UuidOption = new ReadReq.Types.Options.Types.UUIDOption { Structured = new Empty() }
		};
		if (subscribe)
		{
			options.Subscription = new ReadReq.Types.Options.Types.SubscriptionOptions();
		}
		else
		{
			options.Count = 1;
		}

		using var call = StreamsClient.Read(new ReadReq { Options = options }, callOptions);
		await call.ResponseStream.MoveNext();
	}

	private async Task Tombstone(string streamName, CallOptions callOptions)
	{
		using var call = StreamsClient.TombstoneAsync(new TombstoneReq
		{
			Options = new TombstoneReq.Types.Options
			{
				Any = new Empty(),
				StreamIdentifier = StreamIdentifier(streamName)
			}
		}, callOptions);
		await call.ResponseAsync;
	}

	private static async Task<StatusCode> CaptureStatus(Func<Task> action)
	{
		try
		{
			await action();
			return StatusCode.OK;
		}
		catch (RpcException ex)
		{
			return ex.StatusCode;
		}
	}

	private static IEnumerable<SecurityOperation> AllStreamOperations()
	{
		yield return SecurityOperation.ReadEvent;
		yield return SecurityOperation.ReadForward;
		yield return SecurityOperation.ReadBackward;
		yield return SecurityOperation.Write;
		yield return SecurityOperation.ReadMetadata;
		yield return SecurityOperation.WriteMetadata;
		yield return SecurityOperation.Subscribe;
		yield return SecurityOperation.Tombstone;
	}

	private static (string userName, string password) CredentialsFor(SecurityIdentity identity) => identity switch
	{
		SecurityIdentity.UserOne => UserOneCredentials,
		SecurityIdentity.UserTwo => UserTwoCredentials,
		SecurityIdentity.Admin => NamedAdminCredentials,
		SecurityIdentity.Invalid => ("badlogin", "badpass"),
		SecurityIdentity.Anonymous => default,
		_ => throw new ArgumentOutOfRangeException(nameof(identity), identity, null)
	};

	private static StreamIdentifier StreamIdentifier(string streamName) => new()
	{
		StreamName = ByteString.CopyFromUtf8(streamName)
	};

	private static BatchAppendReq.Types.ProposedMessage ProposedBatchMessage() => new()
	{
		Id = Uuid.NewUuid().ToDto(),
		Data = ByteString.CopyFromUtf8("{}"),
		Metadata =
		{
			{ GrpcMetadata.ContentType, GrpcMetadata.ContentTypes.ApplicationJson },
			{ GrpcMetadata.Type, "security-event" }
		}
	};

	private static string AclJson(StreamAclKind acl) => $"{{\"$acl\":{AclBodyJson(acl)}}}";

	private static string AclBodyJson(StreamAclKind acl) => acl switch
	{
		StreamAclKind.UserOne => AclBodyJson(UserOneName),
		StreamAclKind.Admins => AclBodyJson(SystemRoles.Admins),
		StreamAclKind.All => AclBodyJson(SystemRoles.All),
		StreamAclKind.None => "{}",
		_ => throw new ArgumentOutOfRangeException(nameof(acl), acl, null)
	};

	private static string AclBodyJson(string role) =>
		$"{{\"$r\":\"{role}\",\"$w\":\"{role}\",\"$d\":\"{role}\",\"$mr\":\"{role}\",\"$mw\":\"{role}\"}}";
}
