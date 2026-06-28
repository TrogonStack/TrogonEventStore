using System;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexStreamIdTests
{
	[Theory]
	[InlineData("index-stream")]
	[InlineData("$index-stream")]
	public void constructor_accepts_valid_stream_id(string value)
	{
		var streamId = new IndexStreamId(value);

		Assert.Equal(value, streamId.Value);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("$$")]
	public void constructor_rejects_invalid_stream_id(string value)
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexStreamId(value));

		Assert.Equal("value", exception.ParamName);
		Assert.Equal("Index stream id must be a valid stream id. (Parameter 'value')", exception.Message);
	}

	[Fact]
	public void constructor_rejects_metastream_id()
	{
		var exception = Assert.Throws<ArgumentException>(() => new IndexStreamId("$$index-stream"));

		Assert.Equal("value", exception.ParamName);
		Assert.Equal("Index stream id cannot be a metastream id. (Parameter 'value')", exception.Message);
	}

	[Fact]
	public void to_string_returns_stream_id()
	{
		var streamId = new IndexStreamId("$index-stream");

		Assert.Equal("$index-stream", streamId.ToString());
	}
}
