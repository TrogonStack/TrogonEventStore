using System;
using System.Collections.Generic;
using System.IO;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

[Collection("SerilogLoggingTests")]
public class FileSystemArchiveLoggingTests : DirectoryPerTest<FileSystemArchiveLoggingTests>, IDisposable
{
	private readonly ILogger _previousLogger = Log.Logger;
	private readonly CollectingSink _sink = new();

	public FileSystemArchiveLoggingTests()
	{
		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(_sink)
			.CreateLogger();
	}

	[Fact]
	public void reader_logs_the_resolved_archive_path()
	{
		var archivePath = Path.Combine(Fixture.Directory, "nested", "..", "archive-reader");
		Directory.CreateDirectory(Path.GetFullPath(archivePath));

		_ = new FileSystemReader(
			new FileSystemOptions { Path = archivePath },
			CreateChunkNamer(Path.GetFullPath(archivePath)),
			"archive.chk");

		Assert.Contains(_sink.Events,
			x => x.RenderMessage().Contains(Path.GetFullPath(archivePath), StringComparison.Ordinal));
	}

	[Fact]
	public void writer_logs_the_resolved_archive_path()
	{
		var archivePath = Path.Combine(Fixture.Directory, "nested", "..", "archive-writer");
		Directory.CreateDirectory(Path.GetFullPath(archivePath));

		_ = new FileSystemWriter(
			new FileSystemOptions { Path = archivePath },
			"archive.chk");

		Assert.Contains(_sink.Events,
			x => x.RenderMessage().Contains(Path.GetFullPath(archivePath), StringComparison.Ordinal));
	}

	public void Dispose()
	{
		Log.Logger = _previousLogger;
	}

	private static IArchiveChunkNamer CreateChunkNamer(string archivePath)
	{
		var namingStrategy = new VersionedPatternFileNamingStrategy(archivePath, "chunk-");
		return new ArchiveChunkNamer(namingStrategy);
	}

	private sealed class CollectingSink : ILogEventSink
	{
		public List<LogEvent> Events { get; } = [];

		public void Emit(LogEvent logEvent) =>
			Events.Add(logEvent);
	}
}
