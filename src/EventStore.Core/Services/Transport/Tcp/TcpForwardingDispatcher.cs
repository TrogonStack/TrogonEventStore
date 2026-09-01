using System;

namespace EventStore.Core.Services.Transport.Tcp;

public sealed class TcpForwardingDispatcher(TimeSpan writeTimeout) : ClientWriteTcpDispatcher(writeTimeout);
