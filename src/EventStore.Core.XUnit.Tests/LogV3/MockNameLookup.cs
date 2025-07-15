using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.LogAbstraction;
using StreamId = System.UInt32;

namespace EventStore.Core.XUnit.Tests.LogV3;

class MockNameLookup(Dictionary<StreamId, string> dict) : INameLookup<StreamId>
{
	public ValueTask<Optional<StreamId>> TryGetLastValue(CancellationToken token)
	{
		return new ValueTask<Optional<StreamId>>(dict.Count > 0
			? dict.Keys.Max()
			: Optional.None<StreamId>());
	}

	public ValueTask<string> LookupName(StreamId key, CancellationToken token)
	{
		return new ValueTask<string>(dict.TryGetValue(key, out var name)
			? name
			: null);
	}
}
