using System;
using NUnit.Framework;

namespace EventStore.Core.Tests;

[TestFixture]
public class ProcessArchitectureTests
{
	[Test]
	public void server_requires_64_bit_process()
	{
		Assert.That(Environment.Is64BitProcess, Is.True);
	}
}
