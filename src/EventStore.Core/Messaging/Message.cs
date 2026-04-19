using System;
using System.Threading;
using Newtonsoft.Json;

namespace EventStore.Core.Messaging;

[AttributeUsage(AttributeTargets.Class)]
public class BaseMessageAttribute : Attribute {
	public BaseMessageAttribute() {
	}
}

[AttributeUsage(AttributeTargets.Class)]
public class DerivedMessageAttribute : Attribute {
	public DerivedMessageAttribute() {
	}

	public DerivedMessageAttribute(object messageGroup) {
	}
}

[BaseMessage]
public abstract partial class Message {
	protected Message(CancellationToken cancellationToken = default) {
		CancellationToken = cancellationToken;
	}

	[JsonIgnore]
	public CancellationToken CancellationToken { get; }
}
