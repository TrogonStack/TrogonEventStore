using System;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexNameTests
{
	[Theory]
	[InlineData("orders")]
	[InlineData("orders_by_customer")]
	[InlineData("orders-by-customer")]
	[InlineData("orders2")]
	public void constructor_accepts_valid_name(string value)
	{
		var name = new IndexName(value);

		Assert.Equal(value, name.Value);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void constructor_rejects_empty_name(string value)
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexName(value));

		Assert.Equal("value", exception.ParamName);
	}

	[Theory]
	[InlineData("Orders")]
	[InlineData("orders by customer")]
	[InlineData("orders.by.customer")]
	[InlineData("orders/active")]
	public void constructor_rejects_invalid_characters(string value)
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexName(value));

		Assert.Equal("value", exception.ParamName);
	}

	[Fact]
	public void to_string_returns_name()
	{
		var name = new IndexName("orders");

		Assert.Equal("orders", name.ToString());
	}
}
