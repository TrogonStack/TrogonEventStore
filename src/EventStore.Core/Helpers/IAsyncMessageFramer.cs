using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Helpers;

public interface IAsyncMessageFramer<out TMessage>
{
	bool HasData { get; }
	IEnumerable<ArraySegment<byte>> FrameData(ArraySegment<byte> data);
	ValueTask UnFrameData(IEnumerable<ArraySegment<byte>> data, CancellationToken token);
	ValueTask UnFrameData(ArraySegment<byte> data, CancellationToken token);
	void RegisterMessageArrivedCallback(Func<TMessage, CancellationToken, ValueTask> handler);
	void Reset();
}
