using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Node;
using EventStore.Plugins.Authorization;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc;

public sealed class NodeInformation(
	NodeInformationProvider provider,
	IAuthorizationProvider authorizationProvider)
	: EventStore.Client.Node.NodeInformation.NodeInformationBase {
	private static readonly Operation ReadOperation = new(Plugins.Authorization.Operations.Node.Information.Read);
	private static readonly Operation OptionsOperation = new(Plugins.Authorization.Operations.Node.Information.Options);

	public override async Task<NodeInfo> Read(Empty request, ServerCallContext context) {
		if (!await authorizationProvider.CheckAccessAsync(
			    context.GetHttpContext().User,
			    ReadOperation,
			    context.CancellationToken))
			throw RpcExceptions.AccessDenied();

		return provider.Read();
	}

	public override async Task<NodeOptions> Options(Empty request, ServerCallContext context) {
		if (!await authorizationProvider.CheckAccessAsync(
			    context.GetHttpContext().User,
			    OptionsOperation,
			    context.CancellationToken))
			throw RpcExceptions.AccessDenied();

		return provider.Options();
	}
}
