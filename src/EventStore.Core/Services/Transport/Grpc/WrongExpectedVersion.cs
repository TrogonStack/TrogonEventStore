using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Grpc;
using static EventStore.Core.Data.ExpectedVersion;

// ReSharper disable once CheckNamespace
namespace EventStore.Client
{
	partial class WrongExpectedVersion
	{
		public static WrongExpectedVersion Create(long currentVersion,
			long expectedStreamPosition)
		{
			var result = new WrongExpectedVersion
			{
				expectedStreamPositionOption_ = expectedStreamPosition switch
				{
					Any or NoStream or StreamExists =>
						new Google.Protobuf.WellKnownTypes.Empty(),
					_ => StreamRevision.FromInt64(expectedStreamPosition).ToUInt64()
				},
				expectedStreamPositionOptionCase_ = expectedStreamPosition switch
				{
					Any => ExpectedStreamPositionOptionOneofCase.ExpectedAny,
					NoStream => ExpectedStreamPositionOptionOneofCase.ExpectedNoStream,
					StreamExists => ExpectedStreamPositionOptionOneofCase.ExpectedStreamExists,
					_ => ExpectedStreamPositionOptionOneofCase.ExpectedStreamPosition
				}
			};
			if (currentVersion == NoStream)
			{
				result.currentStreamRevisionOption_ = new Google.Protobuf.WellKnownTypes.Empty();
				result.currentStreamRevisionOptionCase_ =
					CurrentStreamRevisionOptionOneofCase.CurrentNoStream;
			}
			else if (currentVersion >= 0)
			{
				result.currentStreamRevisionOption_ = StreamRevision.FromInt64(currentVersion).ToUInt64();
				result.currentStreamRevisionOptionCase_ =
					CurrentStreamRevisionOptionOneofCase.CurrentStreamRevision;
			}
			return result;
		}
	}
}
