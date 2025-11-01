namespace EventStore.Core.Services.Archive.Storage;

public interface IArchiveStorageFactory
{
	IArchiveStorageReader CreateReader();
	IArchiveStorageWriter CreateWriter();
}
