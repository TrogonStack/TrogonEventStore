using System;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Operations;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.OperationsTests;

public class AdminTests {
	private const string OperationsService = "event_store.client.operations.Operations";
	private static readonly (string userName, string password) OpsCredentials = ("ops", "changeit");
	private static readonly Marshaller<Empty> EmptyMarshaller = Marshallers.Create(
		static message => message.ToByteArray(),
		static bytes => Empty.Parser.ParseFrom(bytes));
	private static readonly Marshaller<StartScavengeReq> StartScavengeReqMarshaller = Marshallers.Create(
		static message => message.ToByteArray(),
		static bytes => StartScavengeReq.Parser.ParseFrom(bytes));
	private static readonly Marshaller<ScavengeResp> ScavengeRespMarshaller = Marshallers.Create(
		static message => message.ToByteArray(),
		static bytes => ScavengeResp.Parser.ParseFrom(bytes));
	private static readonly Marshaller<StopScavengeReq> StopScavengeReqMarshaller = Marshallers.Create(
		static message => message.ToByteArray(),
		static bytes => StopScavengeReq.Parser.ParseFrom(bytes));
	private static readonly Marshaller<ScavengeStatusResp> ScavengeStatusRespMarshaller = Marshallers.Create(
		static message => message.ToByteArray(),
		static bytes => ScavengeStatusResp.Parser.ParseFrom(bytes));
	private static readonly Method<StartScavengeReq, ScavengeResp> StartScavengeMethod = new(
		MethodType.Unary,
		OperationsService,
		"StartScavenge",
		StartScavengeReqMarshaller,
		ScavengeRespMarshaller);
	private static readonly Method<StopScavengeReq, ScavengeResp> StopScavengeMethod = new(
		MethodType.Unary,
		OperationsService,
		"StopScavenge",
		StopScavengeReqMarshaller,
		ScavengeRespMarshaller);
	private static readonly Method<Empty, ScavengeStatusResp> GetCurrentScavengeMethod = new(
		MethodType.Unary,
		OperationsService,
		"GetCurrentScavenge",
		EmptyMarshaller,
		ScavengeStatusRespMarshaller);
	private static readonly Method<Empty, ScavengeStatusResp> GetLastScavengeMethod = new(
		MethodType.Unary,
		OperationsService,
		"GetLastScavenge",
		EmptyMarshaller,
		ScavengeStatusRespMarshaller);
	private static readonly Method<Empty, Empty> MergeIndexesMethod = new(
		MethodType.Unary,
		OperationsService,
		"MergeIndexes",
		EmptyMarshaller,
		EmptyMarshaller);
	private static readonly Method<Empty, Empty> ReloadConfigMethod = new(
		MethodType.Unary,
		OperationsService,
		"ReloadConfig",
		EmptyMarshaller,
		EmptyMarshaller);
	private static readonly Marshaller<SetNodePriorityReq> SetNodePriorityReqMarshaller = Marshallers.Create(
		static message => message.ToByteArray(),
		static bytes => SetNodePriorityReq.Parser.ParseFrom(bytes));
	private static readonly Method<SetNodePriorityReq, Empty> SetNodePriorityMethod = new(
		MethodType.Unary,
		OperationsService,
		"SetNodePriority",
		SetNodePriorityReqMarshaller,
		EmptyMarshaller);
	private static readonly Method<Empty, Empty> ResignNodeMethod = new(
		MethodType.Unary,
		OperationsService,
		"ResignNode",
		EmptyMarshaller,
		EmptyMarshaller);
	private static readonly Method<Empty, Empty> ShutdownMethod = new(
		MethodType.Unary,
		OperationsService,
		"Shutdown",
		EmptyMarshaller,
		EmptyMarshaller);

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_merging_indexes_as_admin<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					MergeIndexesMethod,
					null,
					GetCallOptions(AdminCredentials),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_merging_indexes_as_ops<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					MergeIndexesMethod,
					null,
					GetCallOptions(OpsCredentials),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_merging_indexes_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					MergeIndexesMethod,
					null,
					GetCallOptions(),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reloading_config_as_admin<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					ReloadConfigMethod,
					null,
					GetCallOptions(AdminCredentials),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reloading_config_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					ReloadConfigMethod,
					null,
					GetCallOptions(),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_starting_scavenge_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StartScavengeMethod,
					null,
					GetCallOptions(),
					new StartScavengeReq {
						Options = new StartScavengeReq.Types.Options {
							ThreadCount = -1
						}
					});
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_starting_scavenge_from_negative_chunk<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StartScavengeMethod,
					null,
					GetCallOptions(AdminCredentials),
					new StartScavengeReq {
						Options = new StartScavengeReq.Types.Options {
							StartFromChunk = -1
						}
					});
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_invalid_argument() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.InvalidArgument, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_starting_scavenge_with_invalid_threads<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StartScavengeMethod,
					null,
					GetCallOptions(AdminCredentials),
					new StartScavengeReq {
						Options = new StartScavengeReq.Types.Options {
							ThreadCount = -1
						}
					});
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_invalid_argument() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.InvalidArgument, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_starting_scavenge_with_invalid_throttle<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StartScavengeMethod,
					null,
					GetCallOptions(AdminCredentials),
					new StartScavengeReq {
						Options = new StartScavengeReq.Types.Options {
							ThrottlePercent = 0
						}
					});
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_invalid_argument() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.InvalidArgument, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_starting_multithreaded_scavenge_with_throttle<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StartScavengeMethod,
					null,
					GetCallOptions(AdminCredentials),
					new StartScavengeReq {
						Options = new StartScavengeReq.Types.Options {
							ThreadCount = 2,
							ThrottlePercent = 50
						}
					});
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_invalid_argument() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.InvalidArgument, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_stopping_scavenge_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StopScavengeMethod,
					null,
					GetCallOptions(),
					new StopScavengeReq {
						Options = new StopScavengeReq.Types.Options {
							ScavengeId = "current"
						}
					});
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_stopping_scavenge_without_id<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					StopScavengeMethod,
					null,
					GetCallOptions(AdminCredentials),
					new StopScavengeReq());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_invalid_argument() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.InvalidArgument, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_getting_current_scavenge_as_ops<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private ScavengeStatusResp _response;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			_response = await Channel.CreateCallInvoker().AsyncUnaryCall(
				GetCurrentScavengeMethod,
				null,
				GetCallOptions(OpsCredentials),
				new Empty());
		}

		[Test]
		public void returns_stopped() {
			Assert.AreEqual(ScavengeStatusResp.Types.ScavengeStatus.Stopped, _response.ScavengeStatus);
			Assert.IsEmpty(_response.ScavengeId);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_getting_current_scavenge_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					GetCurrentScavengeMethod,
					null,
					GetCallOptions(),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_getting_last_scavenge_as_ops<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private ScavengeStatusResp _response;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			_response = await Channel.CreateCallInvoker().AsyncUnaryCall(
				GetLastScavengeMethod,
				null,
				GetCallOptions(OpsCredentials),
				new Empty());
		}

		[Test]
		public void returns_unknown() {
			Assert.AreEqual(ScavengeStatusResp.Types.ScavengeStatus.Unknown, _response.ScavengeStatus);
			Assert.IsEmpty(_response.ScavengeId);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_getting_last_scavenge_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					GetLastScavengeMethod,
					null,
					GetCallOptions(),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_setting_node_priority_as_admin<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					SetNodePriorityMethod,
					null,
					GetCallOptions(AdminCredentials),
					new SetNodePriorityReq { Priority = 17 });
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_setting_node_priority_as_ops<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					SetNodePriorityMethod,
					null,
					GetCallOptions(OpsCredentials),
					new SetNodePriorityReq { Priority = 17 });
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_setting_node_priority_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					SetNodePriorityMethod,
					null,
					GetCallOptions(),
					new SetNodePriorityReq { Priority = 17 });
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_resigning_node_as_admin<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					ResignNodeMethod,
					null,
					GetCallOptions(AdminCredentials),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_resigning_node_as_ops<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					ResignNodeMethod,
					null,
					GetCallOptions(OpsCredentials),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void completes() {
			Assert.IsNull(_exception);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_resigning_node_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					ResignNodeMethod,
					null,
					GetCallOptions(),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_shutting_down_without_permissions<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private Exception _exception;

		protected override Task Given() => Task.CompletedTask;

		protected override async Task When() {
			try {
				await Channel.CreateCallInvoker().AsyncUnaryCall(
					ShutdownMethod,
					null,
					GetCallOptions(),
					new Empty());
			} catch (Exception ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}
}
