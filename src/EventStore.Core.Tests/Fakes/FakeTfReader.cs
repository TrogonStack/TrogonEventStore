using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog;

namespace EventStore.Core.Tests.Fakes;

public class FakeTfReader : ITransactionFileReader
{
	public void Reposition(long position) => throw new NotImplementedException();

	public SeqReadResult TryReadNext() => throw new NotImplementedException();

	public ValueTask<SeqReadResult> TryReadPrev(CancellationToken token)
		=> ValueTask.FromException<SeqReadResult>(new NotImplementedException());

	public RecordReadResult TryReadAt(long position, bool couldBeScavenged) => throw new NotImplementedException();

	public bool ExistsAt(long position) => true;
}
