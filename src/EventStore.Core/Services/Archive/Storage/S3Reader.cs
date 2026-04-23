using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using EventStore.Common.Exceptions;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using FluentStorage;
using FluentStorage.AWS.Blobs;
using FluentStorage.Blobs;
using Serilog;

namespace EventStore.Core.Services.Archive.Storage;

public class S3Reader : FluentReader, IArchiveStorageReader
{
	private readonly S3Options _options;
	private readonly IAwsS3BlobStorage _awsBlobStorage;

	public S3Reader(S3Options options, IArchiveChunkNamer chunkNamer, string archiveCheckpointFile) : base(
		chunkNamer, archiveCheckpointFile)
	{
		AwsTraceLogging.Configure();
		_options = options;

		if (string.IsNullOrEmpty(options.Bucket))
			throw new InvalidConfigurationException("Please specify an Archive S3 Bucket");

		if (string.IsNullOrEmpty(options.Region))
			throw new InvalidConfigurationException("Please specify an Archive S3 Region");

		_awsBlobStorage = StorageFactory.Blobs.AwsS3(
			awsCliProfileName: options.AwsCliProfileName,
			bucketName: options.Bucket,
			region: options.Region) as IAwsS3BlobStorage;
	}

	protected override ILogger Log { get; } = Serilog.Log.ForContext<S3Reader>();

	protected override IBlobStorage BlobStorage => _awsBlobStorage;

	public async ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(start);

		var length = end - start;
		if (length < 0)
		{
			throw new InvalidOperationException(
				$"Attempted to read negative amount from chunk {chunkFile}. Start: {start}. End {end}");
		}

		if (length == 0)
			return Stream.Null;

		var request = new GetObjectRequest
		{
			BucketName = _options.Bucket, Key = chunkFile, ByteRange = new ByteRange(start, end - 1),
		};

		try
		{
			var client = _awsBlobStorage.NativeBlobClient;
			var response = await client.GetObjectAsync(request, ct);
			return response.ResponseStream;
		}
		catch (AmazonS3Exception ex)
		{
			if (ex.ErrorCode == "NoSuchKey")
				throw new ChunkDeletedException();
			if (ex.ErrorCode == "InvalidRange")
				return Stream.Null;
			throw;
		}
	}

	public override async IAsyncEnumerable<string> ListChunks([EnumeratorCancellation] CancellationToken ct)
	{
		var listResponse = _awsBlobStorage.NativeBlobClient.Paginators.ListObjectsV2(
			new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = ChunkNamer.Prefix });

		await foreach (var s3Object in listResponse.S3Objects.WithCancellation(ct))
			yield return s3Object.Key;
	}
}
