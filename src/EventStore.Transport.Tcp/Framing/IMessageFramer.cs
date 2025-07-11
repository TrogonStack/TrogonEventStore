using System;
using System.Collections.Generic;

namespace EventStore.Transport.Tcp.Framing;

/// <summary>
/// Encodes outgoing messages in frames and decodes incoming frames. 
/// For decoding it uses an internal state, raising a registered 
/// callback, once full message arrives
/// </summary>
public interface IMessageFramer<TMessage>
{
	bool HasData { get; }
	IEnumerable<ArraySegment<byte>> FrameData(ArraySegment<byte> data);
	void UnFrameData(IEnumerable<ArraySegment<byte>> data);
	void UnFrameData(ArraySegment<byte> data);
	void RegisterMessageArrivedCallback(Action<TMessage> handler);
	void Reset();
}
