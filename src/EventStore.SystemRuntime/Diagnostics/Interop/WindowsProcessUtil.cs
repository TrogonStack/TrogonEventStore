// ReSharper disable CheckNamespace

using System.Runtime.InteropServices;
using Serilog;
using RuntimeInformation = System.Runtime.RuntimeInformation;

namespace System.Diagnostics;

/*Courtesy of Eric Johannsen: https://stackoverflow.com/a/20623311*/
public static class WindowsProcessUtil
{
	[StructLayout(LayoutKind.Sequential)]
	struct RM_UNIQUE_PROCESS
	{
		public int dwProcessId;
		public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
	}

	const int RmRebootReasonNone = 0;
	const int CCH_RM_MAX_APP_NAME = 255;
	const int CCH_RM_MAX_SVC_NAME = 63;

	enum RM_APP_TYPE
	{
		RmUnknownApp = 0,
		RmMainWindow = 1,
		RmOtherWindow = 2,
		RmService = 3,
		RmExplorer = 4,
		RmConsole = 5,
		RmCritical = 1000
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct RM_PROCESS_INFO
	{
		public RM_UNIQUE_PROCESS Process;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
		public string strAppName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
		public string strServiceShortName;

		public RM_APP_TYPE ApplicationType;
		public uint AppStatus;
		public uint TSSessionId;
		[MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
	}

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
	static extern int RmRegisterResources(uint pSessionHandle,
		UInt32 nFiles,
		string[] rgsFilenames,
		UInt32 nApplications,
		[In] RM_UNIQUE_PROCESS[] rgApplications,
		UInt32 nServices,
		string[] rgsServiceNames);

	[DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
	static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

	[DllImport("rstrtmgr.dll")]
	static extern int RmEndSession(uint pSessionHandle);

	[DllImport("rstrtmgr.dll")]
	static extern int RmGetList(uint dwSessionHandle,
		out uint pnProcInfoNeeded,
		ref uint pnProcInfo,
		[In, Out] RM_PROCESS_INFO[] rgAffectedApps,
		ref uint lpdwRebootReasons);

	/// <summary>
	/// Find out what process(es) have a lock on the specified file.
	/// </summary>
	/// <param name="path">Path of the file.</param>
	/// <returns>Processes locking the file</returns>
	/// <remarks>See also:
	/// http://msdn.microsoft.com/en-us/library/windows/desktop/aa373661(v=vs.85).aspx
	/// </remarks>
	static List<Process> WhoIsLocking(string path)
	{
		uint handle;
		var key = Guid.NewGuid().ToString();
		List<Process> processes = [];

		var res = RmStartSession(out handle, 0, key);
		if (res != 0)
			throw new Exception("Could not begin restart session.  Unable to determine file locker.");

		try
		{
			const int ERROR_MORE_DATA = 234;

			uint pnProcInfoNeeded = 0,
				pnProcInfo = 0,
				lpdwRebootReasons = RmRebootReasonNone;

			var resources = new string[] { path }; // Just checking on one resource.

			res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null!, 0, null!);

			if (res != 0)
				throw new Exception("Could not register resource.");

			//Note: there's a race condition here -- the first call to RmGetList() returns
			//      the total number of process. However, when we call RmGetList() again to get
			//      the actual processes this number may have increased.
			res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null!, ref lpdwRebootReasons);

			if (res == ERROR_MORE_DATA)
			{
				// Create an array to store the process results
				RM_PROCESS_INFO[] processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
				pnProcInfo = pnProcInfoNeeded;

				// Get the list
				res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
				if (res == 0)
				{
					processes = new((int)pnProcInfo);

					// Enumerate all the results and add them to the
					// list to be returned
					for (var i = 0; i < pnProcInfo; i++)
					{
						try
						{
							processes.Add(Process.GetProcessById(processInfo[i].Process.dwProcessId));
						}
						// catch the error -- in case the process is no longer running
						catch (ArgumentException)
						{
						}
					}
				}
				else
					throw new Exception("Could not list processes locking resource.");
			}
			else if (res != 0)
				throw new Exception("Could not list processes locking resource. Failed to get size of result.");
		}
		finally
		{
			RmEndSession(handle);
		}

		return processes;
	}

	public static bool TryGetWhoIsLocking(string path, out List<Process> processes, out Exception? error)
	{
		if (!RuntimeInformation.IsWindows)
		{
			processes = [];
			error = null;
			return false;
		}

		try
		{
			processes = WhoIsLocking(path);
			error = null;
			return true;
		}
		catch (Exception ex)
		{
			processes = [];
			error = ex;
			return false;
		}
	}

	public static void PrintWhoIsLocking(string path, ILogger logger)
	{
		logger.Information("Trying to retrieve list of processes having a file handle open on {Path} (requires admin privileges)", path);
		if (TryGetWhoIsLocking(path, out var processes, out var error))
			logger.Information(
				$"Processes locking {{Path}}:{Environment.NewLine}{{ProcessList}}",
				path, string.Join(Environment.NewLine, processes.Select(x => $"[{x.Id}] {x.MainModule?.FileName}"))
			);
		else
			logger.Error(error, "Could not retrieve list of processes using file handle {Path}", path);
	}
}
