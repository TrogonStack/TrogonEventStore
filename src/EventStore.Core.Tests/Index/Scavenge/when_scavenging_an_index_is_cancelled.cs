using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Index;
using NUnit.Framework;

namespace EventStore.Core.Tests.Index.Scavenge;

[TestFixture]
public class when_scavenging_an_index_is_cancelled : SpecificationWithDirectoryPerTestFixture
{
	private PTable _oldTable;
	private string _expectedOutputFile;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		var table = new HashListMemTable(PTableVersions.IndexV4, maxSize: 20);
		table.Add(0x010100000000, 0, 1);
		table.Add(0x010200000000, 0, 2);
		table.Add(0x010300000000, 0, 3);
		table.Add(0x010300000000, 1, 4);
		_oldTable = PTable.FromMemtable(table, GetTempFilePath(), Constants.PTableInitialReaderCount, Constants.PTableMaxReaderCountDefault);

		var cancellationTokenSource = new CancellationTokenSource();
		long spaceSaved;
		Func<IndexEntry, bool> existsAt = x =>
		{
			cancellationTokenSource.Cancel();
			return true;
		};

		_expectedOutputFile = GetTempFilePath();
		Assert.That(
			() => PTable.Scavenged(_oldTable, _expectedOutputFile,
				PTableVersions.IndexV4, existsAt, out spaceSaved, ct: cancellationTokenSource.Token,
				initialReaders: Constants.PTableInitialReaderCount, maxReaders: Constants.PTableMaxReaderCountDefault,
				useBloomFilter: true),
			Throws.InstanceOf<OperationCanceledException>());
	}

	[OneTimeTearDown]
	public override Task TestFixtureTearDown()
	{
		_oldTable.Dispose();

		return base.TestFixtureTearDown();
	}

	[Test]
	public void the_output_file_is_deleted()
	{
		Assert.That(File.Exists(_expectedOutputFile), Is.False);
		Assert.That(File.Exists(PTable.GenBloomFilterFilename(_expectedOutputFile)), Is.False);
	}
}
