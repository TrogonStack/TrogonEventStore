using System;
using System.Threading.Tasks;
using EventStore.Client;
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
	private static readonly Method<Empty, Empty> MergeIndexesMethod = new(
		MethodType.Unary,
		OperationsService,
		"MergeIndexes",
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
}
