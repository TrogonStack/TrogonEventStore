using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.LogAbstraction;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.LogV3;

public class PartitionManager(
	ITransactionFileReader reader,
	ITransactionFileWriter writer,
	LogV3RecordFactory recordFactory)
	: IPartitionManager
{
	private static readonly ILogger _log = Serilog.Log.ForContext<PartitionManager>();

	private const string RootPartitionName = "Root";
	private const string RootPartitionTypeName = "Root";

	public Guid? RootId { get; private set; }
	public Guid? RootTypeId { get; private set; }

	public ValueTask Initialize(CancellationToken token)
	{
		if (RootId.HasValue)
			return ValueTask.CompletedTask;

		ReadRootPartition();

		return EnsureRootPartitionIsWritten(token);
	}

	private async ValueTask EnsureRootPartitionIsWritten(CancellationToken token)
	{
		// below code only takes into account offline truncation
		if (!RootTypeId.HasValue)
		{
			RootTypeId = Guid.NewGuid();
			long pos = writer.Position;
			var rootPartitionType = recordFactory.CreatePartitionTypeRecord(
				timeStamp: DateTime.UtcNow,
				logPosition: pos,
				partitionTypeId: RootTypeId.Value,
				partitionId: Guid.Empty,
				name: RootPartitionTypeName);

			if (await writer.Write(rootPartitionType, token) is (false, _))
				throw new Exception($"Failed to write root partition type!");

			writer.Flush();

			_log.Debug("Root partition type created, id: {id}", RootTypeId);
		}

		if (!RootId.HasValue)
		{
			RootId = Guid.NewGuid();
			long pos = writer.Position;
			var rootPartition = recordFactory.CreatePartitionRecord(
				timeStamp: DateTime.UtcNow,
				logPosition: pos,
				partitionId: RootId.Value,
				partitionTypeId: RootTypeId.Value,
				parentPartitionId: Guid.Empty,
				flags: 0,
				referenceNumber: 0,
				name: RootPartitionName);

			if (await writer.Write(rootPartition, token) is (false, _))
				throw new Exception($"Failed to write root partition!");

			writer.Flush();

			recordFactory.SetRootPartitionId(RootId.Value);

			_log.Debug("Root partition created, id: {id}", RootId);
		}
	}

	private void ReadRootPartition()
	{
		SeqReadResult result;
		reader.Reposition(0);
		while ((result = reader.TryReadNext()).Success)
		{
			var rec = result.LogRecord;
			switch (rec.RecordType)
			{
				case LogRecordType.PartitionType:
					var r = ((PartitionTypeLogRecord)rec).Record;
					if (r.StringPayload == RootPartitionTypeName && r.SubHeader.PartitionId == Guid.Empty)
					{
						RootTypeId = r.Header.RecordId;

						_log.Debug("Root partition type read, id: {id}", RootTypeId);

						break;
					}

					throw new InvalidDataException(
						"Unexpected partition type encountered while trying to read the root partition type.");

				case LogRecordType.Partition:
					var p = ((PartitionLogRecord)rec).Record;
					if (p.StringPayload == RootPartitionName && p.SubHeader.PartitionTypeId == RootTypeId
					                                         && p.SubHeader.ParentPartitionId == Guid.Empty)
					{
						RootId = p.Header.RecordId;
						recordFactory.SetRootPartitionId(RootId.Value);

						_log.Debug("Root partition read, id: {id}", RootId);

						return;
					}

					throw new InvalidDataException(
						"Unexpected partition encountered while trying to read the root partition.");

				case LogRecordType.System:
					var systemLogRecord = (ISystemLogRecord)result.LogRecord;
					if (systemLogRecord.SystemRecordType == SystemRecordType.Epoch)
					{
						continue;
					}

					throw new ArgumentOutOfRangeException("SystemRecordType",
						"Unexpected system record while trying to read the root partition");

				default:
					throw new ArgumentOutOfRangeException("RecordType",
						"Unexpected record while trying to read the root partition");
			}
		}
	}
}
