using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

public abstract class ArchiveStorageTestsBase<T> : DirectoryPerTest<T>
{
	protected const string ChunkPrefix = "chunk-";
	private readonly string _bucket = $"archive-contract-{Guid.NewGuid():N}";
	private AmazonS3Client _s3Client;
	protected string ArchivePath => Path.Combine(Fixture.Directory, "archive");
	protected string DbPath => Path.Combine(Fixture.Directory, "db");
	protected abstract StorageType StorageType { get; }

	public ArchiveStorageTestsBase()
	{
		Directory.CreateDirectory(ArchivePath);
		Directory.CreateDirectory(DbPath);
	}

	public override async Task InitializeAsync()
	{
		await base.InitializeAsync();

		if (StorageType != StorageType.S3)
		{
			return;
		}

		var options = CreateS3Options();
		_s3Client = new AmazonS3Client(
			new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
			new AmazonS3Config
			{
				ServiceURL = options.ServiceUrl,
				AuthenticationRegion = options.Region,
				ForcePathStyle = true,
			});

		await _s3Client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket });
	}

	public override async Task DisposeAsync()
	{
		try
		{
			if (_s3Client is not null)
			{
				var objects = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket });
				foreach (var item in objects.S3Objects ?? [])
				{
					await _s3Client.DeleteObjectAsync(_bucket, item.Key);
				}

				await _s3Client.DeleteBucketAsync(_bucket);
			}
		}
		finally
		{
			_s3Client?.Dispose();
			await base.DisposeAsync();
		}
	}

	protected IArchiveStorageFactory CreateSutFactory(
		StorageType storageType,
		IArchiveMetrics archiveMetrics = null)
	{
		var namingStrategy = new VersionedPatternFileNamingStrategy(ArchivePath, ChunkPrefix);
		var chunkNamer = new ArchiveChunkNamer(namingStrategy);
		var factory = new ArchiveStorageFactory(
			new()
			{
				StorageType = storageType,
				S3 = storageType == StorageType.S3 ? CreateS3Options() : new(),
			},
			chunkNamer,
			archiveMetrics);
		return factory;
	}

	private S3Options CreateS3Options() => new()
	{
		Bucket = _bucket,
		Region = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_REGION"),
		AccessKeyId = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_ACCESS_KEY"),
		SecretAccessKey = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_SECRET_KEY"),
		ServiceUrl = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_ENDPOINT"),
	};

	private static string GetRequiredEnvironmentVariable(string name) =>
		Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
			? value
			: throw new InvalidOperationException($"{name} must be configured to run S3 contract tests");

	protected IArchiveStorageWriter CreateWriterSut(StorageType storageType) =>
		CreateSutFactory(storageType).CreateWriter();

	protected IArchiveStorageReader CreateReaderSut(StorageType storageType) =>
		CreateSutFactory(storageType).CreateReader();

	protected static string CreateChunk(string path, int chunkStartNumber, int chunkVersion)
	{
		var namingStrategy = new VersionedPatternFileNamingStrategy(path, ChunkPrefix);

		var chunk = Path.Combine(path, namingStrategy.GetFilenameFor(chunkStartNumber, chunkVersion));
		var content = new byte[1000];
		RandomNumberGenerator.Fill(content);
		File.WriteAllBytes(chunk, content);
		return chunk;
	}

	protected string CreateArchiveChunk(int chunkStartNumber, int chunkVersion) =>
		CreateChunk(ArchivePath, chunkStartNumber, chunkVersion);

	protected string CreateLocalChunk(int chunkStartNumber, int chunkVersion) =>
		CreateChunk(DbPath, chunkStartNumber, chunkVersion);
}
