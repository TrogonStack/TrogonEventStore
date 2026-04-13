// ReSharper disable CheckNamespace

using System.Runtime;
using OsxNative = System.Diagnostics.Interop.OsxNative;
using WindowsNative = System.Diagnostics.Interop.WindowsNative;

namespace System.Diagnostics;

[PublicAPI]
public static class ProcessStats
{
	[Flags]
	private enum LinuxIoField
	{
		None = 0,
		ReadBytes = 1,
		WrittenBytes = 2,
		ReadOps = 4,
		WriteOps = 8,
		All = ReadBytes | WrittenBytes | ReadOps | WriteOps,
	}

	public static DiskIoData GetDiskIo(Process process)
	{
		return RuntimeInformation.OsPlatform switch
		{
			RuntimeOSPlatform.Linux => GetDiskIoLinux(process),
			RuntimeOSPlatform.OSX => GetDiskIoOsx(process),
			RuntimeOSPlatform.Windows => GetDiskIoWindows(process),
			RuntimeOSPlatform.FreeBSD => default,
			_ => throw new NotSupportedException("Operating system not supported")
		};

		static DiskIoData GetDiskIoLinux(Process process)
		{
			var procIoFile = $"/proc/{process.Id}/io";

			try
			{
				return ParseLinuxDiskIo(File.ReadLines(procIoFile));
			}
			catch (Exception ex)
			{
				throw new ApplicationException("Failed to get Linux process I/O info", ex);
			}
		}

		static DiskIoData GetDiskIoOsx(Process process) =>
			OsxNative.IO.GetDiskIo(process.Id);

		static DiskIoData GetDiskIoWindows(Process process) =>
			WindowsNative.IO.GetDiskIo(process);
	}

	public static DiskIoData GetDiskIo() =>
		GetDiskIo(Process.GetCurrentProcess());

	internal static DiskIoData ParseLinuxDiskIo(IEnumerable<string> lines)
	{
		var result = new DiskIoData();
		var seenFields = LinuxIoField.None;

		foreach (var line in lines)
		{
			if (TryExtractIoValue(line, "read_bytes", out var readBytes))
			{
				result = result with { ReadBytes = readBytes };
				seenFields |= LinuxIoField.ReadBytes;
			}
			else if (TryExtractIoValue(line, "write_bytes", out var writeBytes))
			{
				result = result with { WrittenBytes = writeBytes };
				seenFields |= LinuxIoField.WrittenBytes;
			}
			else if (TryExtractIoValue(line, "syscr", out var readOps))
			{
				result = result with { ReadOps = readOps };
				seenFields |= LinuxIoField.ReadOps;
			}
			else if (TryExtractIoValue(line, "syscw", out var writeOps))
			{
				result = result with { WriteOps = writeOps };
				seenFields |= LinuxIoField.WriteOps;
			}

			if (seenFields == LinuxIoField.All)
				break;
		}

		return result;
	}

	private static bool TryExtractIoValue(string line, string key, out ulong value)
	{
		if (line.StartsWith(key))
		{
			var rawValue = line[(key.Length + 1)..].Trim(); // handle the `:` character
			value = Convert.ToUInt64(rawValue);
			return true;
		}

		value = 0;
		return false;
	}
}

/// <summary>
/// Represents a record struct for Disk I/O data.
/// </summary>
public readonly record struct DiskIoData
{
	public DiskIoData() { }

	public DiskIoData(ulong readBytes, ulong writtenBytes, ulong readOps, ulong writeOps)
	{
		ReadBytes = readBytes;
		WrittenBytes = writtenBytes;
		ReadOps = readOps;
		WriteOps = writeOps;
	}

	/// <summary>
	/// Gets or sets the number of bytes read.
	/// </summary>
	public ulong ReadBytes { get; init; }

	/// <summary>
	/// Gets or sets the number of bytes written.
	/// </summary>
	public ulong WrittenBytes { get; init; }

	/// <summary>
	/// Gets or sets the number of read operations.
	/// </summary>
	public ulong ReadOps { get; init; }

	/// <summary>
	/// Gets or sets the number of write operations.
	/// </summary>
	public ulong WriteOps { get; init; }
}
