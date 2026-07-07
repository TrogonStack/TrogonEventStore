using System;
using System.Linq;
using System.Text.Json;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using EventStore.Projections.Core.Messages;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace EventStore.Client.Projections
{
	partial class Projections
	{
		partial class ProjectionsBase : ServiceBase
		{
		}
	}
}

namespace EventStore.Projections.Core.Services.Grpc
{
	internal partial class ProjectionManagement : EventStore.Client.Projections.Projections.ProjectionsBase
	{
		private readonly IPublisher _publisher;
		private readonly IAuthorizationProvider _authorizationProvider;

		public ProjectionManagement(IPublisher publisher, IAuthorizationProvider authorizationProvider)
		{
			if (publisher == null)
			{
				throw new ArgumentNullException(nameof(publisher));
			}

			if (authorizationProvider == null)
			{
				throw new ArgumentNullException(nameof(authorizationProvider));
			}

			_publisher = publisher;
			_authorizationProvider = authorizationProvider;
		}

		private static Exception UnknownMessage<T>(Message message) where T : Message =>
			new RpcException(
				new Status(StatusCode.Unknown,
					$"Envelope callback expected {typeof(T).Name}, received {message.GetType().Name} instead"));
		private static Exception InvalidSubsystemRestart(string state) =>
			new RpcException(
				new Status(StatusCode.FailedPrecondition,
					$"Projection Subsystem cannot be restarted as it is in the wrong state: {state}"));

		private static Exception ProjectionNotFound(string name) =>
			new RpcException(new Status(StatusCode.NotFound, $"Projection '{name}' not found"));

		private static Exception MapFailure(ProjectionManagementMessage.OperationFailed failed) =>
			failed switch
			{
				ProjectionManagementMessage.RecordTooLarge => new RpcException(
					new Status(StatusCode.InvalidArgument, failed.Reason)),
				ProjectionManagementMessage.Conflict => new RpcException(
					new Status(StatusCode.AlreadyExists, failed.Reason)),
				ProjectionManagementMessage.NotAuthorized => new RpcException(
					new Status(StatusCode.PermissionDenied, failed.Reason)),
				_ => new RpcException(new Status(StatusCode.FailedPrecondition, failed.Reason))
			};

		private static Value GetProtoValue(JsonElement element) =>
			element.ValueKind switch
			{
				JsonValueKind.Null => new Value { NullValue = NullValue.NullValue },
				JsonValueKind.Array => new Value
				{
					ListValue = new ListValue
					{
						Values = {
							element.EnumerateArray().Select(GetProtoValue)
						}
					}
				},
				JsonValueKind.False => new Value { BoolValue = false },
				JsonValueKind.True => new Value { BoolValue = true },
				JsonValueKind.String => new Value { StringValue = element.GetString() },
				JsonValueKind.Number => new Value { NumberValue = element.GetDouble() },
				JsonValueKind.Object => new Value { StructValue = GetProtoStruct(element) },
				JsonValueKind.Undefined => new Value(),
				_ => throw new InvalidOperationException()
			};

		private static Struct GetProtoStruct(JsonElement element)
		{
			var structValue = new Struct();
			foreach (var property in element.EnumerateObject())
			{
				structValue.Fields.Add(property.Name, GetProtoValue(property.Value));
			}

			return structValue;
		}

		private static string ToJson<T>(T value) where T : class =>
			value is null ? "{}" : value.ToJson();

		internal static byte[] ToMetadata(MapField<string, string> annotations)
		{
			if (annotations is null || annotations.Count == 0)
			{
				return EventStore.Common.Utils.Empty.ByteArray;
			}

			var metadata = annotations.ToDictionary(
				annotation => annotation.Key,
				annotation => annotation.Value);
			return JsonSerializer.SerializeToUtf8Bytes(metadata);
		}

		private static Value GetProtoValue(string value, bool isJson)
		{
			if (string.IsNullOrEmpty(value))
			{
				return new Value
				{
					StructValue = new Struct()
				};
			}

			if (!isJson)
			{
				return new Value
				{
					StringValue = value
				};
			}

			try
			{
				using var document = JsonDocument.Parse(value);
				return GetProtoValue(document.RootElement);
			}
			catch (JsonException)
			{
				return new Value
				{
					StringValue = value
				};
			}
		}
	}
}
