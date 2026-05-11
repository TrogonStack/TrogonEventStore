using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.LogAbstraction;

public interface IPartitionManager
{
	Guid? RootId { get; }
	Guid? RootTypeId { get; }

	ValueTask Initialize(CancellationToken token);
}
