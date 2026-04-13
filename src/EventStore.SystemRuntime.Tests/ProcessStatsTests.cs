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
	public void linux_parser_treats_zero_values_as_parsed()
	{
		var result = ProcessStats.ParseLinuxDiskIo(ThrowAfter(
			"write_bytes: 0",
			"syscw: 0",
			"read_bytes: 0",
			"syscr: 0"));

		Assert.Equal(new DiskIoData(
			readBytes: 0,
			writtenBytes: 0,
			readOps: 0,
			writeOps: 0), result);
	}

	[Fact]
	public void get_disk_io_returns_without_throwing()
	{
		var result = ProcessStats.GetDiskIo(Process.GetCurrentProcess());

		Assert.IsType<DiskIoData>(result);
	}

	private static IEnumerable<string> ThrowAfter(params string[] lines)
	{
		foreach (var line in lines)
			yield return line;

		throw new InvalidOperationException("Parser read past the point where all counters were already present.");
	}
}
