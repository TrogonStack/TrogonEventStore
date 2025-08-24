using System;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.XUnit.Tests.Services.Archive.ArchiveCatchup;

internal class CustomNamingStrategy : IVersionedFileNamingStrategy
{
	public string GetFilenameFor(int chunkStartNumber, int chunkEndNumber) =>
		$"chunk-{chunkStartNumber}.{chunkEndNumber}";

	public string DetermineBestVersionFilenameFor(int index, int initialVersion) => throw new NotImplementedException();
	public string[] GetAllVersionsFor(int index) => throw new NotImplementedException();
	public string[] GetAllPresentFiles() => throw new NotImplementedException();
	public string GetTempFilename() => throw new NotImplementedException();
	public string[] GetAllTempFiles() => throw new NotImplementedException();

	public int GetIndexFor(string fileName)
	{
		var idx1 = fileName.IndexOf('-') + 1;
		var idx2 = fileName.IndexOf('.');
		return int.Parse(fileName[idx1..idx2]);
	}

	public int GetVersionFor(string fileName)
	{
		var idx = fileName.IndexOf('.') + 1;
		return int.Parse(fileName[idx..]);
	}

	public string GetPrefixFor(int? index, int? version) => throw new NotImplementedException();
}
