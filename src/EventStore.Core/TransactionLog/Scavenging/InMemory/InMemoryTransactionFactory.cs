using System;

namespace EventStore.Core.TransactionLog.Scavenging.InMemory;

public class InMemoryTransactionFactory : ITransactionFactory<int>
{
	int _transactionNumber;

	public InMemoryTransactionFactory()
	{
	}

	public int Begin() => _transactionNumber++;

	public void Commit(int transasction)
	{
	}

	public void Rollback(int transaction) => throw new NotImplementedException();
}
