using System;
using System.Threading;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class IsStreamDeletedShould<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	[Test]
	public void crash_on_null_stream_argument()
	{
		Assert.ThrowsAsync<ArgumentNullException>(async () => await ReadIndex.IsStreamDeleted(null, CancellationToken.None));
	}

	[Test]
	public void throw_on_empty_stream_argument()
	{
		Assert.ThrowsAsync<ArgumentNullException>(async () => await ReadIndex.IsStreamDeleted(string.Empty, CancellationToken.None));
	}
}
