using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using EventStore.Core.Messaging;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.projections_manager.runas
{
	namespace when_posting_a_transient_projection
	{
		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class Authenticated<TLogFormat, TStreamId> : TestFixtureWithProjectionCoreAndManagementServices<TLogFormat, TStreamId>
		{
			private string _projectionName;
			private ClaimsPrincipal _testUserPrincipal;

			private string _projectionBody = @"fromAll().when({$any:function(s,e){return s;}});";

			protected override void Given()
			{
				_projectionName = "test-projection";
				_projectionBody = @"fromAll().when({$any:function(s,e){return s;}});";
				_testUserPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
					new[] {
						new Claim(ClaimTypes.Name,"test-user"),
						new Claim(ClaimTypes.Role,"test-role1"),
						new Claim(ClaimTypes.Role,"test-role2")
					}
					, "ES-Test"));

				AllWritesSucceed();
				NoOtherStreams();
			}

			protected override IEnumerable<WhenStep> When()
			{
				yield return new ProjectionSubsystemMessage.StartComponents(Guid.NewGuid());
				yield return
					new ProjectionManagementMessage.Command.Post(
						GetInputQueue(), ProjectionMode.Transient, _projectionName,
						new ProjectionManagementMessage.RunAs(_testUserPrincipal), "JS", _projectionBody, enabled: true,
						checkpointsEnabled: true, emitEnabled: true, trackEmittedStreams: true, enableRunAs: true);
			}

			[Test, Ignore("ignored")]
			public void anonymous_cannot_retrieve_projection_query()
			{
				GetInputQueue()
					.Publish(
						new ProjectionManagementMessage.Command.GetQuery(
							Envelope, _projectionName, ProjectionManagementMessage.RunAs.Anonymous));
				_queue.Process();

				Assert.IsTrue(HandledMessages.OfType<ProjectionManagementMessage.NotAuthorized>().Any());
			}

			[Test]
			public void projection_owner_can_retrieve_projection_query()
			{
				GetInputQueue()
					.Publish(
						new ProjectionManagementMessage.Command.GetQuery(
							Envelope, _projectionName, new ProjectionManagementMessage.RunAs(_testUserPrincipal)));
				_queue.Process();

				var query = HandledMessages.OfType<ProjectionManagementMessage.ProjectionQuery>().FirstOrDefault();
				Assert.NotNull(query);
				Assert.AreEqual(_projectionBody, query.Query);
			}
		}

		[TestFixture(typeof(LogFormat.V2), typeof(string))]
		[TestFixture(typeof(LogFormat.V3), typeof(uint))]
		public class Anonymous<TLogFormat, TStreamId> : TestFixtureWithProjectionCoreAndManagementServices<TLogFormat, TStreamId>
		{
			private string _projectionName;

			private string _projectionBody = @"fromAll().when({$any:function(s,e){return s;}});";

			protected override void Given()
			{
				_projectionName = "test-projection";
				_projectionBody = @"fromAll().when({$any:function(s,e){return s;}});";

				AllWritesSucceed();
				NoOtherStreams();
			}

			protected override IEnumerable<WhenStep> When()
			{
				yield return new ProjectionSubsystemMessage.StartComponents(Guid.NewGuid());
				yield return
					new ProjectionManagementMessage.Command.Post(
						GetInputQueue(), ProjectionMode.Continuous, _projectionName,
						ProjectionManagementMessage.RunAs.Anonymous, "JS", _projectionBody, enabled: true,
						checkpointsEnabled: true, emitEnabled: true, trackEmittedStreams: true, enableRunAs: true);
			}

			[Test]
			public void replies_with_not_authorized()
			{
				Assert.IsTrue(HandledMessages.OfType<ProjectionManagementMessage.NotAuthorized>().Any());
			}
		}
	}
}
