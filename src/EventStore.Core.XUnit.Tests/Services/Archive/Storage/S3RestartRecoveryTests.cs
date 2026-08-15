using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

#if RUN_S3_TESTS
public class S3RestartRecoveryTests
{
	private const string ArchiveCheckpointFile = "archive.chk";
	private const string ChunkFile = "chunk-000000.000000";
	private const long Checkpoint = 4_194_304;
	private static readonly byte[] Payload = Enumerable.Range(0, 4096).Select(x => (byte)(x % 251)).ToArray();

	[Fact]
	public async Task executes_the_requested_restart_recovery_phase()
	{
		var phase = GetRequiredEnvironmentVariable("EVENTSTORE_S3_RECOVERY_PHASE");
		var options = CreateOptions();

		switch (phase)
		{
			case "seed":
				await Seed(options);
				break;
			case "unavailable":
				await AssertUnavailable(options);
				break;
			case "verify-cleanup":
				await VerifyAndCleanup(options);
				break;
			default:
				throw new InvalidOperationException($"Unsupported restart recovery phase: {phase}");
		}
	}

	private static async Task Seed(S3Options options)
	{
		using var client = CreateClient(options);
		await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket });

		var localChunk = Path.GetTempFileName();
		try
		{
			await File.WriteAllBytesAsync(localChunk, Payload);
			var writer = new S3Writer(options, ArchiveCheckpointFile);
			Assert.True(await writer.StoreChunk(localChunk, ChunkFile, CancellationToken.None));
			Assert.True(await writer.SetCheckpoint(Checkpoint, CancellationToken.None));
		}
		finally
		{
			File.Delete(localChunk);
		}
	}

	private static async Task AssertUnavailable(S3Options options)
	{
		var reader = CreateReader(options);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var stopwatch = Stopwatch.StartNew();

		await Assert.ThrowsAnyAsync<Exception>(async () =>
			await reader.GetCheckpoint(timeout.Token));

		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Storage unavailability was not detected within the bounded interval: {stopwatch.Elapsed}");
	}

	private static async Task VerifyAndCleanup(S3Options options)
	{
		using var client = CreateClient(options);
		try
		{
			var reader = CreateReader(options);
			Assert.Equal(Checkpoint, await reader.GetCheckpoint(CancellationToken.None));

			await using var chunk = await reader.GetChunk(ChunkFile, CancellationToken.None);
			using var copy = new MemoryStream();
			await chunk.CopyToAsync(copy);
			Assert.Equal(Payload, copy.ToArray());
		}
		finally
		{
			var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket });
			foreach (var item in objects.S3Objects ?? [])
			{
				await client.DeleteObjectAsync(options.Bucket, item.Key);
			}
			await client.DeleteBucketAsync(options.Bucket);
		}
	}

	private static S3Reader CreateReader(S3Options options)
	{
		var namingStrategy = new VersionedPatternFileNamingStrategy(Path.GetTempPath(), "chunk-");
		return new S3Reader(options, new ArchiveChunkNamer(namingStrategy), ArchiveCheckpointFile);
	}

	private static AmazonS3Client CreateClient(S3Options options) =>
		new(
			new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
			new AmazonS3Config
			{
				ServiceURL = options.ServiceUrl,
				AuthenticationRegion = options.Region,
				ForcePathStyle = true,
			});

	private static S3Options CreateOptions() => new()
	{
		Bucket = GetRequiredEnvironmentVariable("EVENTSTORE_S3_RECOVERY_BUCKET"),
		Region = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_REGION"),
		AccessKeyId = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_ACCESS_KEY"),
		SecretAccessKey = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_SECRET_KEY"),
		ServiceUrl = GetRequiredEnvironmentVariable("EVENTSTORE_S3_TEST_ENDPOINT"),
	};

	private static string GetRequiredEnvironmentVariable(string name) =>
		Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
			? value
			: throw new InvalidOperationException($"{name} must be configured to run the S3 restart recovery gate");
}
#endif
