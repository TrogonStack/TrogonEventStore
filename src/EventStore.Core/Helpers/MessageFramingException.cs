using System;

namespace EventStore.Core.Helpers;

public sealed class MessageFramingException : Exception
{
	public MessageFramingException(string message) : base(message)
	{
	}
}
