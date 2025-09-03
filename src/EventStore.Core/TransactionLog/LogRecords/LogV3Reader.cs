using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext.IO;
using EventStore.LogCommon;

namespace EventStore.Core.TransactionLog.LogRecords;

public static class LogV3Reader
{
	public static async ValueTask<byte[]> ReadBytes(LogRecordType type, byte version, IAsyncBinaryReader reader,
		int recordLength, CancellationToken token)
	{
		// todo: if we could get some confidence that we would return to the pool
		// (e.g. with reference counting) then we could use arraypool here. or just maybe a ring buffer
		// var bytes = ArrayPool<byte>.Shared.Rent(length);
		var bytes = new byte[recordLength];
		bytes[0] = (byte)type;
		bytes[1] = version;
		await reader.ReadAsync(bytes.AsMemory(2..recordLength), token);
		return bytes;
	}
}
