using System;
using System.Collections.Generic;
using EventStore.Core.Services.Archive.Archiver;
using EventStore.Core.Services.Archive.Archiver.Unmerger;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Plugins;
using EventStore.Plugins.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventStore.Core.Services.Archive;

public class ArchivePlugableComponent(bool isArchiver) : IPlugableComponent
{
	public string Name => "Archiver";

	public string DiagnosticsName => Name;

	public KeyValuePair<string, object>[] DiagnosticsTags => [];

	public string Version => "0.0.1";

	public bool Enabled { get; private set; }

	public string LicensePublicKey => LicenseConstants.LicensePublicKey;

	public void ConfigureApplication(IApplicationBuilder builder, IConfiguration configuration)
	{
		if (!Enabled)
			return;

		_ = builder.ApplicationServices.GetService<ArchiverService>();
	}

	public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
	{
		var options = configuration.GetSection("EventStore:Archive").Get<ArchiveOptions>() ?? new ArchiveOptions();
		Enabled = options.Enabled;

		if (!Enabled)
			return;

		services.AddSingleton(options);
		services.AddScoped<IArchiveStorageFactory, ArchiveStorageFactory>();
		services.Decorate<IReadOnlyList<IClusterVNodeStartupTask>>(AddArchiveCatchupTask);
		services.AddSingleton<IArchiveChunkNamer, ArchiveChunkNamer>();

		if (isArchiver)
		{
			services.AddSingleton<IChunkUnmerger, ChunkUnmerger>();
			services.AddSingleton<ArchiverService>();
		}
	}

	private static IReadOnlyList<IClusterVNodeStartupTask> AddArchiveCatchupTask(
		IReadOnlyList<IClusterVNodeStartupTask> startupTasks,
		IServiceProvider serviceProvider)
	{

		var newStartupTasks = new List<IClusterVNodeStartupTask>();
		if (startupTasks != null)
			newStartupTasks.AddRange(startupTasks);

		var standardComponents = serviceProvider.GetRequiredService<StandardComponents>();
		newStartupTasks.Add(new ArchiveCatchup.ArchiveCatchup(
			dbPath: standardComponents.DbConfig.Path,
			writerCheckpoint: standardComponents.DbConfig.WriterCheckpoint,
			replicationCheckpoint: standardComponents.DbConfig.ReplicationCheckpoint,
			chunkSize: standardComponents.DbConfig.ChunkSize,
			serviceProvider.GetRequiredService<IVersionedFileNamingStrategy>(),
			serviceProvider.GetRequiredService<IArchiveStorageFactory>()));

		return newStartupTasks;
	}
}
