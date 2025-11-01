namespace EventStore.Core.TransactionLog.Scavenging.InMemory;

public class InMemoryTransactionManager(InMemoryScavengeMap<Unit, ScavengeCheckpoint> storage)
	: TransactionManager<int>(new InMemoryTransactionFactory(), storage);
