using System;
using EventStore.Core.Data;
using EventStore.Core.Services.Transport.Common;

namespace EventStore.Core.Services.Transport.Grpc;

internal readonly record struct CurrentStreamVersion
{
	private enum VersionKind
	{
		Unknown,
		NoStream,
		Known
	}

	private readonly VersionKind _kind;
	private readonly StreamRevision _revision;

	private CurrentStreamVersion(VersionKind kind, StreamRevision revision)
	{
		_kind = kind;
		_revision = revision;
	}

	public static CurrentStreamVersion Unknown { get; } = default;
	public static CurrentStreamVersion NoStream { get; } = new(VersionKind.NoStream, default);

	public static CurrentStreamVersion Known(StreamRevision revision)
	{
		if (revision == StreamRevision.End)
			throw new ArgumentOutOfRangeException(nameof(revision));
		return new CurrentStreamVersion(VersionKind.Known, revision);
	}

	public static CurrentStreamVersion FromInt64(long value) => value switch
	{
		ExpectedVersion.NoStream => NoStream,
		>= 0 => Known(StreamRevision.FromInt64(value)),
		_ => Unknown
	};

	public bool IsNoStream => _kind == VersionKind.NoStream;

	public bool TryGetKnownRevision(out StreamRevision revision)
	{
		revision = _revision;
		return _kind == VersionKind.Known;
	}
}
