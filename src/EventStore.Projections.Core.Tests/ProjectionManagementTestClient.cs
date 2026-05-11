using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Projections;
using Grpc.Core;
using Grpc.Net.Client;
using ClientEmpty = EventStore.Client.Empty;
using ProjectionGrpc = EventStore.Client.Projections.Projections;

namespace EventStore.Projections.Core.Tests;

internal sealed class ProjectionManagementTestClient : IDisposable {
	private readonly GrpcChannel _channel;
	private readonly HttpClient _ownedHttpClient;
	private readonly ProjectionGrpc.ProjectionsClient _client;

	public ProjectionManagementTestClient(IPEndPoint endpoint, HttpMessageHandler httpHandler) {
		_channel = GrpcChannel.ForAddress(
			new Uri($"https://{endpoint}"),
			new GrpcChannelOptions {
				HttpHandler = httpHandler
			});
		_client = new ProjectionGrpc.ProjectionsClient(_channel);
	}

	public ProjectionManagementTestClient(IPEndPoint endpoint, HttpClient httpClient) {
		_ownedHttpClient = httpClient;
		_channel = GrpcChannel.ForAddress(
			httpClient.BaseAddress ?? new Uri($"https://{endpoint}"),
			new GrpcChannelOptions {
				HttpClient = httpClient
			});
		_client = new ProjectionGrpc.ProjectionsClient(_channel);
	}

	public Task CreateContinuous(string name, string query, bool emitEnabled = false, bool trackEmittedStreams = false) =>
		_client.CreateAsync(new CreateReq {
			Options = new CreateReq.Types.Options {
				Continuous = new CreateReq.Types.Options.Types.Continuous {
					Name = name,
					EmitEnabled = emitEnabled,
					TrackEmittedStreams = trackEmittedStreams
				},
				Query = query
			}
		}, GetCallOptions()).ResponseAsync;

	public Task CreateOneTime(string query, string name = null) =>
		_client.CreateAsync(new CreateReq {
			Options = new CreateReq.Types.Options {
				OneTime = new ClientEmpty(),
				Name = name ?? string.Empty,
				Query = query
			}
		}, GetCallOptions()).ResponseAsync;

	public Task CreateTransient(string name, string query) =>
		_client.CreateAsync(new CreateReq {
			Options = new CreateReq.Types.Options {
				Transient = new CreateReq.Types.Options.Types.Transient {
					Name = name
				},
				Query = query
			}
		}, GetCallOptions()).ResponseAsync;

	public Task Enable(string name) =>
		_client.EnableAsync(new EnableReq {
			Options = new EnableReq.Types.Options {
				Name = name
			}
		}, GetCallOptions()).ResponseAsync;

	public Task Disable(string name, bool writeCheckpoint = true) =>
		_client.DisableAsync(new DisableReq {
			Options = new DisableReq.Types.Options {
				Name = name,
				WriteCheckpoint = writeCheckpoint
			}
		}, GetCallOptions()).ResponseAsync;

	public Task Abort(string name) =>
		_client.AbortAsync(new AbortReq {
			Options = new AbortReq.Types.Options {
				Name = name
			}
		}, GetCallOptions()).ResponseAsync;

	public Task UpdateQuery(string name, string query) =>
		_client.UpdateAsync(new UpdateReq {
			Options = new UpdateReq.Types.Options {
				Name = name,
				Query = query
			}
		}, GetCallOptions()).ResponseAsync;

	public async Task<IReadOnlyList<StatisticsResp.Types.Details>> Statistics(
		StatisticsReq.Types.Options options,
		CancellationToken cancellationToken = default) {
		using var call = _client.Statistics(new StatisticsReq {
			Options = options
		}, GetCallOptions(TimeSpan.FromSeconds(20)));

		var results = new List<StatisticsResp.Types.Details>();
		while (await call.ResponseStream.MoveNext(cancellationToken)) {
			results.Add(call.ResponseStream.Current.Details);
		}

		return results;
	}

	public Task<IReadOnlyList<StatisticsResp.Types.Details>> StatisticsAll(CancellationToken cancellationToken = default) =>
		Statistics(new StatisticsReq.Types.Options {
			All = new ClientEmpty()
		}, cancellationToken);

	public Task<IReadOnlyList<StatisticsResp.Types.Details>> StatisticsOneTime(CancellationToken cancellationToken = default) =>
		Statistics(new StatisticsReq.Types.Options {
			OneTime = new ClientEmpty()
		}, cancellationToken);

	public Task<IReadOnlyList<StatisticsResp.Types.Details>> StatisticsContinuous(CancellationToken cancellationToken = default) =>
		Statistics(new StatisticsReq.Types.Options {
			Continuous = new ClientEmpty()
		}, cancellationToken);

	public void Dispose() {
		_channel.Dispose();
		_ownedHttpClient?.Dispose();
	}

	private static CallOptions GetCallOptions(TimeSpan? deadline = null) {
		var credentials = CallCredentials.FromInterceptor((_, metadata) => {
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});

		return new CallOptions(
			credentials: credentials,
			deadline: deadline is { } value
				? DateTime.UtcNow.Add(value)
				: null);
	}
}
