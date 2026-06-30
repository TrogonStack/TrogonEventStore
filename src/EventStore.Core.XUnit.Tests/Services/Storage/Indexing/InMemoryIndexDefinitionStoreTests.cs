using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class InMemoryIndexDefinitionStoreTests
{
	[Fact]
	public async Task read_returns_null_when_missing()
	{
		var store = new InMemoryIndexDefinitionStore();

		var definition = await store.Read(new IndexName("orders"), CancellationToken.None);

		Assert.Null(definition);
	}

	[Fact]
	public async Task create_stores_definition()
	{
		var store = new InMemoryIndexDefinitionStore();
		var name = new IndexName("orders");
		var definition = CreateDefinition("event.type == 'order'");

		var result = await store.Create(name, definition, CancellationToken.None);
		var stored = await store.Read(name, CancellationToken.None);

		Assert.Equal(IndexDefinitionCreateResult.Created, result);
		Assert.Equal(new StoredIndexDefinition(name, definition), stored);
	}

	[Fact]
	public async Task create_is_idempotent_for_same_definition()
	{
		var store = new InMemoryIndexDefinitionStore();
		var name = new IndexName("orders");
		var definition = CreateDefinition("event.type == 'order'");

		await store.Create(name, definition, CancellationToken.None);
		var result = await store.Create(name, definition, CancellationToken.None);
		var stored = await store.Read(name, CancellationToken.None);

		Assert.Equal(IndexDefinitionCreateResult.AlreadyExists, result);
		Assert.Equal(definition, stored.Definition);
	}

	[Fact]
	public async Task create_is_idempotent_for_equivalent_definition()
	{
		var store = new InMemoryIndexDefinitionStore();
		var name = new IndexName("orders");
		var first = CreateDefinition("event.type == 'order'");
		var second = CreateDefinition("event.type == 'order'");

		await store.Create(name, first, CancellationToken.None);
		var result = await store.Create(name, second, CancellationToken.None);
		var stored = await store.Read(name, CancellationToken.None);

		Assert.Equal(IndexDefinitionCreateResult.AlreadyExists, result);
		Assert.Equal(first, stored.Definition);
	}

	[Fact]
	public async Task create_reports_conflict_for_different_definition()
	{
		var store = new InMemoryIndexDefinitionStore();
		var name = new IndexName("orders");
		var first = CreateDefinition("event.type == 'order'");
		var second = CreateDefinition("event.type == 'invoice'");

		await store.Create(name, first, CancellationToken.None);
		var result = await store.Create(name, second, CancellationToken.None);
		var stored = await store.Read(name, CancellationToken.None);

		Assert.Equal(IndexDefinitionCreateResult.Conflicts, result);
		Assert.Equal(first, stored.Definition);
	}

	[Fact]
	public async Task list_returns_stored_definitions()
	{
		var store = new InMemoryIndexDefinitionStore();
		var orders = new StoredIndexDefinition(new IndexName("orders"), CreateDefinition("event.type == 'order'"));
		var invoices = new StoredIndexDefinition(new IndexName("invoices"), CreateDefinition("event.type == 'invoice'"));

		await store.Create(orders.Name, orders.Definition, CancellationToken.None);
		await store.Create(invoices.Name, invoices.Definition, CancellationToken.None);
		var definitions = await store.List(CancellationToken.None);

		Assert.Equal([invoices, orders], definitions);
	}

	[Fact]
	public async Task list_returns_snapshot()
	{
		var store = new InMemoryIndexDefinitionStore();
		var orders = new StoredIndexDefinition(new IndexName("orders"), CreateDefinition("event.type == 'order'"));

		await store.Create(orders.Name, orders.Definition, CancellationToken.None);
		var first = await store.List(CancellationToken.None);
		await store.Delete(orders.Name, CancellationToken.None);
		var second = await store.List(CancellationToken.None);

		Assert.Equal([orders], first);
		Assert.Empty(second);
	}

	[Fact]
	public async Task delete_removes_definition()
	{
		var store = new InMemoryIndexDefinitionStore();
		var name = new IndexName("orders");

		await store.Create(name, CreateDefinition("event.type == 'order'"), CancellationToken.None);
		var deleted = await store.Delete(name, CancellationToken.None);
		var stored = await store.Read(name, CancellationToken.None);

		Assert.True(deleted);
		Assert.Null(stored);
	}

	[Fact]
	public async Task delete_returns_false_when_missing()
	{
		var store = new InMemoryIndexDefinitionStore();

		var deleted = await store.Delete(new IndexName("orders"), CancellationToken.None);

		Assert.False(deleted);
	}

	[Fact]
	public async Task create_rejects_missing_name()
	{
		var store = new InMemoryIndexDefinitionStore();

		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			store.Create(null, CreateDefinition("event.type == 'order'"), CancellationToken.None).AsTask());

		Assert.Equal("name", exception.ParamName);
	}

	[Fact]
	public async Task create_rejects_missing_definition()
	{
		var store = new InMemoryIndexDefinitionStore();

		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			store.Create(new IndexName("orders"), null, CancellationToken.None).AsTask());

		Assert.Equal("definition", exception.ParamName);
	}

	[Fact]
	public async Task read_rejects_missing_name()
	{
		var store = new InMemoryIndexDefinitionStore();

		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			store.Read(null, CancellationToken.None).AsTask());

		Assert.Equal("name", exception.ParamName);
	}

	[Fact]
	public async Task delete_rejects_missing_name()
	{
		var store = new InMemoryIndexDefinitionStore();

		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			store.Delete(null, CancellationToken.None).AsTask());

		Assert.Equal("name", exception.ParamName);
	}

	[Fact]
	public async Task create_honors_cancelled_token()
	{
		var store = new InMemoryIndexDefinitionStore();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			store.Create(new IndexName("orders"), CreateDefinition("event.type == 'order'"), cancellation.Token).AsTask());
	}

	[Fact]
	public async Task read_honors_cancelled_token()
	{
		var store = new InMemoryIndexDefinitionStore();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			store.Read(new IndexName("orders"), cancellation.Token).AsTask());
	}

	[Fact]
	public async Task list_honors_cancelled_token()
	{
		var store = new InMemoryIndexDefinitionStore();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			store.List(cancellation.Token).AsTask());
	}

	[Fact]
	public async Task delete_honors_cancelled_token()
	{
		var store = new InMemoryIndexDefinitionStore();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			store.Delete(new IndexName("orders"), cancellation.Token).AsTask());
	}

	private static IndexDefinition CreateDefinition(string filter) =>
		new(new IndexEventFilter(filter), [new IndexFieldDefinition("id", new IndexFieldSelector("event.body.id"))]);
}
