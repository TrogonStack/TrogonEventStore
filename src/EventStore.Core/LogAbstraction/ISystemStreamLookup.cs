using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.LogAbstraction;

public interface ISystemStreamLookup<TStreamId> : IMetastreamLookup<TStreamId>
{
	TStreamId AllStream { get; }
	TStreamId SettingsStream { get; }
	ValueTask<bool> IsSystemStream(TStreamId streamId, CancellationToken token);
}
