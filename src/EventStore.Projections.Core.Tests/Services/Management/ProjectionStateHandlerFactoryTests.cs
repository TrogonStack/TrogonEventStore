using System;
using EventStore.Projections.Core.Services.Management;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.Management;

[TestFixture]
public class ProjectionStateHandlerFactoryTests
{
	[Test]
	public void missing_native_handler_type_reports_the_requested_type()
	{
		var factory = new ProjectionStateHandlerFactory(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

		var ex = Assert.Throws<NotSupportedException>(() =>
			factory.Create("native:EventStore.Projections.Core.MissingProjectionHandler", "", false, null));

		Assert.That(ex.Message, Does.Contain("EventStore.Projections.Core.MissingProjectionHandler"));
	}
}
