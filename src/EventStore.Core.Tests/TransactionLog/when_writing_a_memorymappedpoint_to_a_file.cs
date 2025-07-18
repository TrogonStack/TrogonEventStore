using System;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Checkpoint;
using NUnit.Framework;
using RuntimeInformation = System.Runtime.RuntimeInformation;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_writing_a_memorymappedpoint_to_a_file : SpecificationWithFile
{
	public override async Task SetUp()
	{
		await base.SetUp();
		if (!RuntimeInformation.IsWindows)
		{
			Assert.Ignore($"{nameof(MemoryMappedFileCheckpoint)} is for windows only.");
		}
	}

	[Test]
	public void a_null_file_throws_argumentnullexception()
	{
		Assert.Throws<ArgumentNullException>(() => new MemoryMappedFileCheckpoint(null));
	}

	[Test]
	public void name_is_set()
	{
		var checksum = new MemoryMappedFileCheckpoint(Filename, "test");
		Assert.AreEqual("test", checksum.Name);
		checksum.Close(flush: true);
	}

	[Test]
	public void reading_off_same_instance_gives_most_up_to_date_info()
	{
		var checkSum = new MemoryMappedFileCheckpoint(Filename);
		checkSum.Write(0xDEAD);
		checkSum.Flush();
		var read = checkSum.Read();
		checkSum.Close(flush: true);
		Assert.AreEqual(0xDEAD, read);
	}

	[Test]
	public void can_read_existing_checksum()
	{
		var checksum = new MemoryMappedFileCheckpoint(Filename);
		checksum.Write(0xDEAD);
		checksum.Close(flush: true);
		checksum = new MemoryMappedFileCheckpoint(Filename);
		var val = checksum.Read();
		checksum.Close(flush: true);
		Assert.AreEqual(0xDEAD, val);
	}

	[Test]
	public async Task the_new_value_is_not_accessible_if_not_flushed_even_with_delay()
	{
		var checkSum = new MemoryMappedFileCheckpoint(Filename);
		checkSum.Write(1011);
		await Task.Delay(200);
		Assert.AreEqual(0, checkSum.Read());
		checkSum.Close(flush: true);
	}

	[Test]
	public async Task the_new_value_is_accessible_after_flush()
	{
		var checkSum = new MemoryMappedFileCheckpoint(Filename);
		checkSum.Write(1011);
		checkSum.Flush();
		Assert.AreEqual(1011, checkSum.Read());
		checkSum.Close(flush: true);
		await Task.Delay(100);
	}
}
