using System;
using System.Threading.Tasks;
using EventStore.Core.Messaging;

namespace EventStore.ClusterNode.Components.Services;

internal sealed class TaskCompletionEnvelope<T> : IEnvelope where T : Message {
	private readonly Func<Message, T> _mapReply;
	private readonly Func<Message, string> _mapFailure;
	private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public TaskCompletionEnvelope(
		Func<Message, T> mapReply = null,
		Func<Message, string> mapFailure = null) {
		_mapReply = mapReply ?? (_ => null);
		_mapFailure = mapFailure ?? (_ => null);
	}

	public Task<T> Task => _source.Task;

	public void ReplyWith<U>(U message) where U : Message {
		if (message is T typed) {
			_source.TrySetResult(typed);
			return;
		}

		var mapped = _mapReply(message);
		if (mapped is not null) {
			_source.TrySetResult(mapped);
			return;
		}

		var failure = _mapFailure(message);
		if (failure is not null) {
			_source.TrySetException(new InvalidOperationException(FailureMessage(message, failure)));
			return;
		}

		_source.TrySetException(new InvalidOperationException(
			$"Expected {typeof(T).Name} but received {message.GetType().Name}."));
	}

	private static string FailureMessage(Message message, string failure) =>
		string.IsNullOrWhiteSpace(failure)
			? $"{message.GetType().Name} failed without a reason."
			: failure;
}
