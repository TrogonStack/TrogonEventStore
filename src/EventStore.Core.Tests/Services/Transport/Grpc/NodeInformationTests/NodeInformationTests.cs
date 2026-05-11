using System.Linq;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Node;
using EventStore.Common.Utils;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using NUnit.Framework;
using ClientEmpty = EventStore.Client.Empty;
using NodeInformationClient = EventStore.Client.Node.NodeInformation.NodeInformationClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.NodeInformationTests;

public class NodeInformationTests
{
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_node_information<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
	{
		private NodeInformationClient _client;
		private NodeInfo _response;

		protected override Task Given()
		{
			_client = new NodeInformationClient(Channel);
			return Task.CompletedTask;
		}

		protected override async Task When() =>
			_response = await _client.ReadAsync(new ClientEmpty(), GetCallOptions());

		[Test]
		public void returns_the_server_version() =>
			Assert.AreEqual(VersionInfo.Version, _response.EsVersion);

		[Test]
		public void returns_feature_metadata() =>
			Assert.IsTrue(_response.Features.ContainsKey("projections"));

		[Test]
		public void returns_authentication_metadata() =>
			Assert.IsNotEmpty(_response.Authentication.Type);
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_node_options_as_admin<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
	{
		private NodeInformationClient _client;
		private NodeOptions _response;

		protected override Task Given()
		{
			_client = new NodeInformationClient(Channel);
			return Task.CompletedTask;
		}

		protected override async Task When() =>
			_response = await _client.OptionsAsync(new ClientEmpty(), GetCallOptions(AdminCredentials));

		[Test]
		public void hides_sensitive_option_values()
		{
			var sensitiveOption = _response.Options.First(x => x.Name == "DefaultAdminPassword");
			Assert.AreEqual("********", sensitiveOption.Value);
		}
	}
}
