using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using EventStore.Core;
using FluentAssertions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Configuration;

public class ClusterVNodeDbPathTests
{
	public static TheoryData<Type> WriteFailureExceptions => [
		typeof(UnauthorizedAccessException),
		typeof(IOException)
	];

	[Theory]
	[MemberData(nameof(WriteFailureExceptions))]
	public void falls_back_when_default_database_directory_cannot_be_written(Type exceptionType)
	{
		var rootPath = CreateTemporaryDirectory();

		try
		{
			var defaultDataDirectory = Path.Combine(rootPath, "default-db");
			var fallbackDefaultDataDirectory = Path.Combine(rootPath, "fallback-db");
			var probedPaths = new List<string>();

			var actual = InvokeEnsureWritableDbPath(
				defaultDataDirectory,
				defaultDataDirectory,
				fallbackDefaultDataDirectory,
				dbPath => {
					probedPaths.Add(dbPath);

					if (dbPath == defaultDataDirectory)
						throw CreateWriteFailure(exceptionType);
				});

			actual.Should().Be(fallbackDefaultDataDirectory);
			Directory.Exists(fallbackDefaultDataDirectory).Should().BeTrue();
			probedPaths.Should().Equal(defaultDataDirectory, fallbackDefaultDataDirectory);
		}
		finally
		{
			DeleteTemporaryDirectory(rootPath);
		}
	}

	[Theory]
	[MemberData(nameof(WriteFailureExceptions))]
	public void rethrows_when_custom_database_directory_cannot_be_written(Type exceptionType)
	{
		var rootPath = CreateTemporaryDirectory();

		try
		{
			var customDataDirectory = Path.Combine(rootPath, "custom-db");
			var defaultDataDirectory = Path.Combine(rootPath, "default-db");
			var fallbackDefaultDataDirectory = Path.Combine(rootPath, "fallback-db");
			var probedPaths = new List<string>();

			var act = () => InvokeEnsureWritableDbPath(
				customDataDirectory,
				defaultDataDirectory,
				fallbackDefaultDataDirectory,
				dbPath => {
					probedPaths.Add(dbPath);
					throw CreateWriteFailure(exceptionType);
				});

			Assert.Throws(exceptionType, act);
			probedPaths.Should().Equal(customDataDirectory);
			Directory.Exists(fallbackDefaultDataDirectory).Should().BeFalse();
		}
		finally
		{
			DeleteTemporaryDirectory(rootPath);
		}
	}

	private static Exception CreateWriteFailure(Type exceptionType) =>
		exceptionType == typeof(UnauthorizedAccessException)
			? new UnauthorizedAccessException("write denied")
			: exceptionType == typeof(IOException)
				? new IOException("write denied")
				: throw new ArgumentOutOfRangeException(nameof(exceptionType), exceptionType, "Unsupported write failure.");

	private static string InvokeEnsureWritableDbPath(
		string dbPath,
		string defaultDataDirectory,
		string fallbackDefaultDataDirectory,
		Action<string> probeWriteAccess)
	{
		var method = typeof(ClusterVNode<string>).GetMethod(
			"EnsureWritableDbPath",
			BindingFlags.NonPublic | BindingFlags.Static,
			null,
			[
				typeof(string),
				typeof(string),
				typeof(string),
				typeof(Action<string>)
			],
			null);

		if (method is null)
			throw new InvalidOperationException("Could not find ClusterVNode<TStreamId>.EnsureWritableDbPath.");

		try
		{
			return (string)method.Invoke(
				obj: null,
				[
					dbPath,
					defaultDataDirectory,
					fallbackDefaultDataDirectory,
					probeWriteAccess
				])!;
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw new UnreachableException();
		}
	}

	private static string CreateTemporaryDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), $"ESX-{nameof(ClusterVNodeDbPathTests)}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteTemporaryDirectory(string path)
	{
		if (Directory.Exists(path))
			Directory.Delete(path, recursive: true);
	}
}
