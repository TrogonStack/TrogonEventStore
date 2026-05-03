using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Http.Controllers;
using EventStore.Core.Tests.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Http.Users
{
	namespace users
	{
		public abstract class with_admin_user<TLogFormat, TStreamId> : HttpBehaviorSpecification<TLogFormat, TStreamId>
		{
			protected readonly NetworkCredential _admin = DefaultData.AdminNetworkCredentials;

			protected override bool GivenSkipInitializeStandardUsersCheck()
			{
				return false;
			}

			public with_admin_user()
			{
				SetDefaultCredentials(_admin);
			}

			protected async Task CreateUser(
				string loginName,
				string fullName,
				string[] groups,
				string password,
				NetworkCredential credentials)
			{
				using var channel = GrpcChannel.ForAddress(new Uri($"https://{_node.HttpEndPoint}"),
					new GrpcChannelOptions
					{
						HttpClient = _node.HttpClient,
						DisposeHttpClient = false
					});
				var users = new UsersClient(channel);
				await users.CreateAsync(new CreateReq
				{
					Options = new CreateReq.Types.Options
					{
						LoginName = loginName,
						FullName = fullName,
						Groups = {groups},
						Password = password
					}
				}, new CallOptions(new Metadata
				{
					{"authorization", BasicAuthHeader(credentials)}
				}));
			}

			protected async Task DisableUser(string loginName, NetworkCredential credentials)
			{
				using var channel = GrpcChannel.ForAddress(new Uri($"https://{_node.HttpEndPoint}"),
					new GrpcChannelOptions
					{
						HttpClient = _node.HttpClient,
						DisposeHttpClient = false
					});
				var users = new UsersClient(channel);
				await users.DisableAsync(new DisableReq
				{
					Options = new DisableReq.Types.Options
					{
						LoginName = loginName
					}
				}, new CallOptions(new Metadata
				{
					{"authorization", BasicAuthHeader(credentials)}
				}));
			}

			private static string BasicAuthHeader(NetworkCredential credentials) =>
				"Basic " + Convert.ToBase64String(
					Encoding.ASCII.GetBytes($"{credentials.UserName}:{credentials.Password}"));
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		class when_retrieving_a_user_details<TLogFormat, TStreamId> : with_admin_user<TLogFormat, TStreamId>
		{
			private JObject _response;

			protected override async Task Given()
			{
				await CreateUser("test1", "User Full Name", new[] {"admin", "other"}, "Pa55w0rd!", _admin);
			}

			protected override async Task When()
			{
				_response = await GetJson<JObject>("/users/test1");
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void returns_valid_json_data()
			{
				HelperExtensions.AssertJson(
					new
					{
						Success = true,
						Error = "Success",
						Data =
							new
							{
								LoginName = "test1",
								FullName = "User Full Name",
								Groups = new[] { "admin", "other" },
								Disabled = false,
								Password___ = false,
								Links = new[] {
									new {
										Href = "http://" + _node.HttpEndPoint + "/users/test1",
										Rel = "edit"
									}
								}
							}
					}, _response);
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		class when_retrieving_a_disabled_user_details<TLogFormat, TStreamId> : with_admin_user<TLogFormat, TStreamId>
		{
			private JObject _response;

			protected override async Task Given()
			{
				await CreateUser("test2", "User Full Name", new[] {"admin", "other"}, "Pa55w0rd!", _admin);
				await DisableUser("test2", _admin);
			}

			protected override async Task When()
			{
				_response = await GetJson<JObject>("/users/test2");
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _lastResponse.StatusCode);
			}

			[Test]
			public void returns_valid_json_data()
			{
				HelperExtensions.AssertJson(
					new
					{
						Success = true,
						Error = "Success",
						Data =
							new
							{
								Links = new[] {
									new {
										Href = "http://" + _node.HttpEndPoint + "/users/test2",
										Rel = "edit"
									}
								}
							}
					}, _response);
			}
		}

		[Category("LongRunning")]
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		class when_updating_user_details<TLogFormat, TStreamId> : with_admin_user<TLogFormat, TStreamId>
		{
			private HttpResponseMessage _response;

			protected override async Task Given()
			{
				await CreateUser("test1", "User Full Name", Array.Empty<string>(), "Pa55w0rd!", _admin);
			}

			protected override async Task When()
			{
				_response = await MakeRawJsonPut("/users/test1", new { FullName = "Updated Full Name" }, _admin);
			}

			[Test]
			public void returns_ok_status_code()
			{
				Assert.AreEqual(HttpStatusCode.OK, _response.StatusCode);
			}

			[Test]
			public async Task updates_full_name()
			{
				var jsonResponse = await GetJson<JObject>("/users/test1");
				HelperExtensions.AssertJson(
					new { Success = true, Error = "Success", Data = new { FullName = "Updated Full Name" } }, jsonResponse);
			}
		}

	}
}
