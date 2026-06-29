using System;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexDefinitionTests
{
	[Fact]
	public void constructor_rejects_missing_fields()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new IndexDefinition(new IndexEventFilter("event.type == 'order'"), null!));

		Assert.Equal("fields", exception.ParamName);
	}

	[Fact]
	public void constructor_rejects_null_field()
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexDefinition(new IndexEventFilter("event.type == 'order'"), [null!]));

		Assert.Equal("fields", exception.ParamName);
	}

	[Fact]
	public void constructor_requires_filter_or_field()
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexDefinition(null, []));

		Assert.Equal("fields", exception.ParamName);
	}

	[Fact]
	public void constructor_accepts_filter_without_fields()
	{
		var filter = new IndexEventFilter("event.type == 'order'");
		var definition = new IndexDefinition(filter, []);

		Assert.Same(filter, definition.Filter);
		Assert.Empty(definition.Fields);
	}

	[Fact]
	public void constructor_accepts_field_without_filter()
	{
		var field = new IndexFieldDefinition("customerId", new IndexFieldSelector("event.body.customerId"));
		var definition = new IndexDefinition(null, [field]);

		Assert.Null(definition.Filter);
		Assert.Equal([field], definition.Fields);
	}

	[Fact]
	public void constructor_copies_fields()
	{
		var field = new IndexFieldDefinition("customerId", new IndexFieldSelector("event.body.customerId"));
		var fields = new[] { field };
		var definition = new IndexDefinition(null, fields);

		fields[0] = new IndexFieldDefinition("tenantId", new IndexFieldSelector("event.body.tenantId"));

		Assert.Equal([field], definition.Fields);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void filter_rejects_empty_value(string value)
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexEventFilter(value));

		Assert.Equal("value", exception.ParamName);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void field_rejects_empty_name(string name)
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexFieldDefinition(name, new IndexFieldSelector("event.body.customerId")));

		Assert.Equal("name", exception.ParamName);
	}

	[Fact]
	public void field_rejects_missing_selector()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new IndexFieldDefinition("customerId", null!));

		Assert.Equal("selector", exception.ParamName);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void field_selector_rejects_empty_value(string value)
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexFieldSelector(value));

		Assert.Equal("value", exception.ParamName);
	}
}
