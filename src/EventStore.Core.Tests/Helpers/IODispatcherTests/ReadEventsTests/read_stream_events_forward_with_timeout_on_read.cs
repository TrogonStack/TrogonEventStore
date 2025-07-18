using System;
using System.Threading;
using EventStore.Core.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Helpers.IODispatcherTests.ReadEventsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class async_read_stream_events_forward_with_timeout_on_read<TLogFormat, TStreamId> : with_read_io_dispatcher<TLogFormat, TStreamId>
{
	private bool _didTimeout;
	private bool _didReceiveRead;

	[OneTimeSetUp]
	public override void TestFixtureSetUp()
	{
		base.TestFixtureSetUp();

		var mre = new ManualResetEvent(false);
		var step = _ioDispatcher.BeginReadForward(
			_cancellationScope, _eventStreamId, _fromEventNumber, _maxCount, true, _principal,
			res =>
			{
				_didReceiveRead = true;
				mre.Set();
			},
			() =>
			{
				_didTimeout = true;
				mre.Set();
			}
		);
		IODispatcherAsync.Run(step);
		Assert.IsNotNull(_timeoutMessage, "Expected TimeoutMessage to not be null");

		_timeoutMessage.Reply();
		mre.WaitOne(TimeSpan.FromSeconds(10));
	}

	[Test]
	public void should_call_timeout_handler()
	{
		Assert.IsTrue(_didTimeout);
	}

	[Test]
	public void should_ignore_read_complete()
	{
		Assert.IsFalse(_didReceiveRead, "Should not have received read completed before replying on message");
		_readForward.Envelope.ReplyWith(CreateReadStreamEventsForwardCompleted(_readForward));
		Assert.IsFalse(_didReceiveRead);
	}
}

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class read_stream_events_forward_with_timeout_on_read<TLogFormat, TStreamId> : with_read_io_dispatcher<TLogFormat, TStreamId>
{
	private bool _didTimeout;
	private bool _didReceiveRead;

	[OneTimeSetUp]
	public override void TestFixtureSetUp()
	{
		base.TestFixtureSetUp();
		var mre = new ManualResetEvent(false);
		_ioDispatcher.ReadForward(
			_eventStreamId, _fromEventNumber, _maxCount, true, _principal,
			res =>
			{
				_didReceiveRead = true;
				mre.Set();
			},
			() =>
			{
				_didTimeout = true;
				mre.Set();
			},
			Guid.NewGuid()
		);
		Assert.IsNotNull(_timeoutMessage, "Expected TimeoutMessage to not be null");

		_timeoutMessage.Reply();
		mre.WaitOne(TimeSpan.FromSeconds(10));
	}

	[Test]
	public void should_call_timeout_handler()
	{
		Assert.IsTrue(_didTimeout);
	}

	[Test]
	public void should_ignore_read_complete()
	{
		Assert.IsFalse(_didReceiveRead, "Should not have received read completed before replying on message");
		_readForward.Envelope.ReplyWith(CreateReadStreamEventsForwardCompleted(_readForward));
		Assert.IsFalse(_didReceiveRead);
	}
}
