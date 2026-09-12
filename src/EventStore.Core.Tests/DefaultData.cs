using System.Net;
using EventStore.Core.Services;

namespace EventStore.Core.Tests;

public class DefaultData
{
	public static string AdminUsername = SystemUsers.Admin;
	public static string AdminPassword = SystemUsers.DefaultAdminPassword;
	public static NetworkCredential AdminNetworkCredentials = new NetworkCredential(AdminUsername, AdminPassword);
	public static ClusterVNodeOptions.DefaultUserOptions DefaultUserOptions = new ClusterVNodeOptions.DefaultUserOptions()
	{
		DefaultAdminPassword = SystemUsers.DefaultAdminPassword,
		DefaultOpsPassword = SystemUsers.DefaultOpsPassword
	};
}
