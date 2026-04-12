using System.Diagnostics;

namespace EventStore.SystemRuntime.Tests;

public class ProcessStatsTests
{
	[Fact]
	public void linux_parser_keeps_reading_after_write_ops()
	{
		var lines = new[]
		{
			"rchar: 1",
			"write_bytes: 22",
			"syscw: 44",
			"read_bytes: 11",
			"syscr: 33",
		};

		var result = ProcessStats.ParseLinuxDiskIo(lines);

		Assert.Equal(new DiskIoData(
			readBytes: 11,
			writtenBytes: 22,
			readOps: 33,
			writeOps: 44), result);
	}

	[Fact]
	public void get_disk_io_returns_without_throwing()
	{
		var result = ProcessStats.GetDiskIo(Process.GetCurrentProcess());

		Assert.IsType<DiskIoData>(result);
	}
}
