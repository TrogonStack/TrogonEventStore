namespace EventStore.Core.TransactionLog.FileNamingStrategy {
	public interface IVersionedFileNamingStrategy {
		string Prefix { get; }
		string GetFilenameFor(int index, int version);
		string DetermineNewVersionFilenameForIndex(int index, int defaultVersion);
		string[] GetAllVersionsFor(int index);
		string[] GetAllPresentFiles();
		string CreateTempFilename();
		string[] GetAllTempFiles();
		int GetIndexFor(string fileName);
		int GetVersionFor(string fileName);
	}
}
