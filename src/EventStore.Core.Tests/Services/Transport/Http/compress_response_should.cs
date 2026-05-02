using System;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using EventStore.Transport.Http;
using EventStore.Transport.Http.EntityManagement;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture]
class compress_response_should
{
	private string inputData = "my test string 123456.";

	[Test]
	public void with_gzip_compression_algo_data_is_gzipped()
	{
		var response =
			HttpEntityManager.CompressResponse(Encoding.ASCII.GetBytes(inputData), CompressionAlgorithms.Gzip);

		String uncompressed;

		using (var inputStream = new MemoryStream(response))
		using (var uncompressedStream = new GZipStream(inputStream, CompressionMode.Decompress))
		using (var outputStream = new MemoryStream())
		{
			uncompressedStream.CopyTo(outputStream);
			uncompressed = Encoding.UTF8.GetString(outputStream.ToArray());
		}

		Assert.AreEqual(uncompressed, inputData);
	}

	[Test]
	public void with_gzip_compression_algo_and_string_larger_than_50kb_data_is_gzipped()
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < 60 * 1024; i++)
			sb.Append("A");
		String testString = sb.ToString();

		var response =
			HttpEntityManager.CompressResponse(Encoding.ASCII.GetBytes(testString), CompressionAlgorithms.Gzip);

		String uncompressed;

		using (var inputStream = new MemoryStream(response))
		using (var uncompressedStream = new GZipStream(inputStream, CompressionMode.Decompress))
		using (var outputStream = new MemoryStream())
		{
			uncompressedStream.CopyTo(outputStream);
			uncompressed = Encoding.UTF8.GetString(outputStream.ToArray());
		}

		Assert.AreEqual(uncompressed, testString);
	}

	[Test]
	public void with_gzip_compression_algo_empty_data_is_valid_gzip()
	{
		var response =
			HttpEntityManager.CompressResponse(Array.Empty<byte>(), CompressionAlgorithms.Gzip);

		Assert.Greater(response.Length, 0);

		String uncompressed;

		using (var inputStream = new MemoryStream(response))
		using (var uncompressedStream = new GZipStream(inputStream, CompressionMode.Decompress))
		using (var outputStream = new MemoryStream())
		{
			uncompressedStream.CopyTo(outputStream);
			uncompressed = Encoding.UTF8.GetString(outputStream.ToArray());
		}

		Assert.AreEqual(uncompressed, string.Empty);
	}

	[Test]
	public void empty_gzip_reply_has_a_valid_gzip_body()
	{
		using var completed = new ManualResetEventSlim();
		using var responseBody = new MemoryStream();
		var context = new DefaultHttpContext();
		context.Request.Scheme = "http";
		context.Request.Host = new HostString("localhost");
		context.Request.Path = "/";
		context.Request.Headers["Accept-Encoding"] = CompressionAlgorithms.Gzip;
		context.Response.Body = responseBody;
		var entity = new HttpEntity(context, logHttpRequests: false, advertiseAsHost: null, advertiseAsPort: 0,
			completed.Set);
		var manager = entity.CreateManager();
		Exception error = null;

		manager.Reply(Array.Empty<byte>(), 200, "OK", "text/plain", Encoding.UTF8, null, ex => error = ex);

		Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
		Assert.IsNull(error);
		Assert.AreEqual(CompressionAlgorithms.Gzip, context.Response.Headers["Content-Encoding"].ToString());
		Assert.Greater(responseBody.Length, 0);

		responseBody.Position = 0;
		using var uncompressedStream = new GZipStream(responseBody, CompressionMode.Decompress);
		using var outputStream = new MemoryStream();
		uncompressedStream.CopyTo(outputStream);
		Assert.AreEqual(0, outputStream.Length);
	}

	[Test]
	public void with_deflate_compression_algo_data_is_deflated()
	{
		var response =
			HttpEntityManager.CompressResponse(Encoding.ASCII.GetBytes(inputData), CompressionAlgorithms.Deflate);

		String uncompressed;

		using (var inputStream = new MemoryStream(response))
		using (var uncompressedStream = new DeflateStream(inputStream, CompressionMode.Decompress))
		using (var outputStream = new MemoryStream())
		{
			uncompressedStream.CopyTo(outputStream);
			uncompressed = Encoding.UTF8.GetString(outputStream.ToArray());
		}

		Assert.AreEqual(uncompressed, inputData);
	}

	[Test]
	public void with_deflate_compression_algo_and_string_larger_than_50kb_data_is_deflated()
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < 60 * 1024; i++)
			sb.Append("A");
		String testString = sb.ToString();

		var response =
			HttpEntityManager.CompressResponse(Encoding.ASCII.GetBytes(testString), CompressionAlgorithms.Deflate);

		String uncompressed;

		using (var inputStream = new MemoryStream(response))
		using (var uncompressedStream = new DeflateStream(inputStream, CompressionMode.Decompress))
		using (var outputStream = new MemoryStream())
		{
			uncompressedStream.CopyTo(outputStream);
			uncompressed = Encoding.UTF8.GetString(outputStream.ToArray());
		}

		Assert.AreEqual(uncompressed, testString);
	}

	[Test]
	public void with_deflate_compression_algo_empty_data_is_valid_deflate()
	{
		var response =
			HttpEntityManager.CompressResponse(Array.Empty<byte>(), CompressionAlgorithms.Deflate);

		Assert.Greater(response.Length, 0);

		String uncompressed;

		using (var inputStream = new MemoryStream(response))
		using (var uncompressedStream = new DeflateStream(inputStream, CompressionMode.Decompress))
		using (var outputStream = new MemoryStream())
		{
			uncompressedStream.CopyTo(outputStream);
			uncompressed = Encoding.UTF8.GetString(outputStream.ToArray());
		}

		Assert.AreEqual(uncompressed, string.Empty);
	}

	[Test]
	public void with_invalid_compression_algo_data_remains_the_same()
	{
		var response =
			HttpEntityManager.CompressResponse(Encoding.ASCII.GetBytes(inputData), "invalid_compression_algo");
		Assert.AreEqual(Encoding.ASCII.GetString(response), inputData);
	}

	[Test]
	public void with_null_compression_algo_data_remains_the_same()
	{
		var response = HttpEntityManager.CompressResponse(Encoding.ASCII.GetBytes(inputData), null);
		Assert.AreEqual(Encoding.ASCII.GetString(response), inputData);
	}
}
