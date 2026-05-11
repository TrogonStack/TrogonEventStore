using System;
using EventStore.Core.Messaging;
using EventStore.Projections.Core.Messages;

namespace EventStore.Projections.Core.Messaging;

[DerivedMessage(ProjectionMessage.Misc)]
public partial class UnwrapEnvelopeMessage : Message
{
	private readonly Action _action;
	private readonly string _extraInformation;

	public UnwrapEnvelopeMessage(Action action, string extraInformation)
	{
		_action = action;
		_extraInformation = extraInformation;
	}

	public Action Action
	{
		get { return _action; }
	}

	public override string ToString()
	{
		return $"{base.ToString()}, Extra Information: {_extraInformation}";
	}
}
