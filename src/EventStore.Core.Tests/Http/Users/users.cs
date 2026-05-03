using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Http.Controllers;
using Grpc.Core;
using Grpc.Net.Client;
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
	}
}
