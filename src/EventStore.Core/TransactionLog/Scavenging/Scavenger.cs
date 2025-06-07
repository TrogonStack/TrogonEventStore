using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Runtime.CompilerServices;
using EventStore.Core.Data;
using EventStore.Core.TransactionLog.Chunks;
using Serilog;

namespace EventStore.Core.TransactionLog.Scavenging;

public class Scavenger<TStreamId>(
	ILogger logger,
	Action checkPreconditions,
	IScavengeState<TStreamId> state,
	IAccumulator<TStreamId> accumulator,
	ICalculator<TStreamId> calculator,
	IChunkExecutor<TStreamId> chunkExecutor,
	IChunkMerger chunkMerger,
	IIndexExecutor<TStreamId> indexExecutor,
	ICleaner cleaner,
	IScavengePointSource scavengePointSource,
	ITFChunkScavengerLog scavengerLogger,
	IScavengeStatusTracker statusTracker,
	int thresholdForNewScavenge,
	bool syncOnly,
	Func<string> getThrottleStats)
	: IScavenger
{
	private readonly Dictionary<string, TimeSpan> _recordedTimes = new();

	public string ScavengeId => scavengerLogger.ScavengeId;

	public void Dispose() =>
		state.Dispose();

	// following old scavenging design the returned task must complete successfully
	[AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder<>))] // get off the main queue
	public async Task<ScavengeResult> ScavengeAsync(CancellationToken cancellationToken)
	{
		_recordedTimes.Clear();
		var stopwatch = Stopwatch.StartNew();
		var result = ScavengeResult.Success;
		string error = null;
		try
		{
			logger.Debug("SCAVENGING: Scavenge Initializing State.");
			state.Init();
			state.LogStats();
			logger.Debug("SCAVENGING: Scavenge Started.");
			LogCollisions();

			scavengerLogger.ScavengeStarted();

			await RunInternal(scavengerLogger, stopwatch, cancellationToken);

			logger.Debug(
				"SCAVENGING: Scavenge Completed. Total time taken: {elapsed}. Total space saved: {spaceSaved}.",
				stopwatch.Elapsed, scavengerLogger.SpaceSaved);

		}
		catch (OperationCanceledException)
		{
			logger.Information("SCAVENGING: Scavenge Stopped. Total time taken: {elapsed}.",
				stopwatch.Elapsed);
			result = ScavengeResult.Stopped;
		}
		catch (Exception exc)
		{
			result = ScavengeResult.Errored;
			logger.Error(exc, "SCAVENGING: Scavenge Failed. Total time taken: {elapsed}.",
				stopwatch.Elapsed);
			error = string.Format("Error while scavenging DB: {0}.", exc.Message);
		}
		finally
		{
			try
			{
				scavengerLogger.ScavengeCompleted(result, error, stopwatch.Elapsed);
				LogCollisions();
				LogTimes();
			}
			catch (Exception ex)
			{
				logger.Error(
					ex,
					"SCAVENGING: Error whilst recording scavenge completed. " +
					"Scavenge result: {result}, Elapsed: {elapsed}, Original error: {e}",
					result, stopwatch.Elapsed, error);
			}
		}

		return result;
	}

	private void LogCollisions()
	{
		var collisions = state.AllCollisions().ToArray();
		logger.Debug("SCAVENGING: {count} KNOWN COLLISIONS", collisions.Length);

		foreach (var collision in collisions)
		{
			logger.Debug("SCAVENGING: KNOWN COLLISION: \"{collision}\"", collision);
		}
	}

	private async Task RunInternal(
		ITFChunkScavengerLog scavengerLogger,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		checkPreconditions();

		// each component can be started with either
		//  (i) a checkpoint that it wrote previously (it will continue from there)
		//  (ii) fresh from a given scavengepoint
		//
		// so if we have a checkpoint from a component, start the scavenge by passing the checkpoint
		// into that component and then starting each subsequent component fresh for that
		// scavengepoint.
		//
		// otherwise, start the whole scavenge fresh from whichever scavengepoint is applicable.
		if (!state.TryGetCheckpoint(out var checkpoint))
		{
			// there is no checkpoint, so this is the first scavenge of this scavenge state
			// (not necessarily the first scavenge of this database, old scavenged may have been run
			// or new scavenges run and the scavenge state deleted)
			logger.Debug("SCAVENGING: Started a new scavenge with no checkpoint");
			await StartNewAsync(
				prevScavengePoint: null,
				scavengerLogger,
				stopwatch,
				cancellationToken);

		}
		else if (checkpoint is ScavengeCheckpoint.Done done)
		{
			// start of a subsequent scavenge.
			logger.Debug("SCAVENGING: Started a new scavenge after checkpoint {checkpoint}", checkpoint);
			await StartNewAsync(
				prevScavengePoint: done.ScavengePoint,
				scavengerLogger,
				stopwatch,
				cancellationToken);

		}
		else
		{
			// the other cases are continuing an incomplete scavenge
			logger.Debug("SCAVENGING: Continuing a scavenge from {checkpoint}", checkpoint);

			if (checkpoint is ScavengeCheckpoint.Accumulating accumulating)
			{
				await Time(stopwatch, "Accumulation", cancellationToken =>
						accumulator.Accumulate(accumulating, state, cancellationToken)
					, cancellationToken);
				await AfterAccumulation(
					accumulating.ScavengePoint, scavengerLogger, stopwatch, cancellationToken);

			}
			else if (checkpoint is ScavengeCheckpoint.Calculating<TStreamId> calculating)
			{
				await Time(stopwatch, "Calculation", cancellationToken =>
				{
					calculator.Calculate(calculating, state, cancellationToken);
					return ValueTask.CompletedTask;
				}, cancellationToken);
				await AfterCalculation(
					calculating.ScavengePoint, scavengerLogger, stopwatch, cancellationToken);

			}
			else if (checkpoint is ScavengeCheckpoint.ExecutingChunks executingChunks)
			{
				await Time(stopwatch, "Chunk execution", cancellationToken =>
						chunkExecutor.Execute(executingChunks, state, scavengerLogger, cancellationToken),
					cancellationToken);
				await AfterChunkExecution(
					executingChunks.ScavengePoint, scavengerLogger, stopwatch, cancellationToken);

			}
			else if (checkpoint is ScavengeCheckpoint.MergingChunks mergingChunks)
			{
				await Time(stopwatch, "Chunk merging", cancellationToken =>
						chunkMerger.MergeChunks(mergingChunks, state, scavengerLogger, cancellationToken),
					cancellationToken);
				await AfterChunkMerging(
					mergingChunks.ScavengePoint, scavengerLogger, stopwatch, cancellationToken);

			}
			else if (checkpoint is ScavengeCheckpoint.ExecutingIndex executingIndex)
			{
				await Time(stopwatch, "Index execution", cancellationToken =>
				{
					indexExecutor.Execute(executingIndex, state, scavengerLogger, cancellationToken);
					return ValueTask.CompletedTask;
				}, cancellationToken);
				await AfterIndexExecution(
					executingIndex.ScavengePoint, stopwatch, cancellationToken);

			}
			else if (checkpoint is ScavengeCheckpoint.Cleaning cleaning)
			{
				await Time(stopwatch, "Cleaning", cancellationToken =>
				{
					cleaner.Clean(cleaning, state, cancellationToken);
					return ValueTask.CompletedTask;
				}, cancellationToken);
				AfterCleaning(cleaning.ScavengePoint);

			}
			else
			{
				throw new Exception($"Unexpected checkpoint: {checkpoint}");
			}
		}
	}

	private async ValueTask Time(Stopwatch stopwatch, string name, Func<CancellationToken, ValueTask> f,
		CancellationToken token)
	{
		using var _ = statusTracker.StartActivity(name);
		logger.Debug("SCAVENGING: Scavenge " + name + " Phase Started.");
		var start = stopwatch.Elapsed;
		await f(token);
		var elapsed = stopwatch.Elapsed - start;
		state.LogStats();
		logger.Debug($"SCAVENGING: {getThrottleStats()}");
		logger.Debug("SCAVENGING: Scavenge " + name + " Phase Completed. Took {elapsed}.", elapsed);
		_recordedTimes[name] = elapsed;
	}

	private void LogTimes()
	{
		foreach (var key in _recordedTimes.Keys.OrderBy(x => x))
		{
			logger.Debug("SCAVENGING: {name} took {elapsed}", key, _recordedTimes[key]);
		}
	}

	private async Task StartNewAsync(
		ScavengePoint prevScavengePoint,
		ITFChunkScavengerLog scavengerLogger,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		// prevScavengePoint is the previous one that was completed
		// latestScavengePoint is the latest one in the database
		// nextScavengePoint is the one we are about to scavenge up to

		ScavengePoint nextScavengePoint;
		var latestScavengePoint = await scavengePointSource
			.GetLatestScavengePointOrDefaultAsync(cancellationToken);
		if (latestScavengePoint == null)
		{
			if (syncOnly)
			{
				logger.Debug("SCAVENGING: No existing scavenge point to sync with, nothing to do.");
				return;
			}
			else
			{
				logger.Debug("SCAVENGING: Creating the first scavenge point.");
				// no latest scavenge point, create the first one
				nextScavengePoint = await scavengePointSource
					.AddScavengePointAsync(
						ExpectedVersion.NoStream,
						threshold: thresholdForNewScavenge,
						cancellationToken);
			}
		}
		else
		{
			// got the latest scavenge point
			if (prevScavengePoint == null ||
			    prevScavengePoint.EventNumber < latestScavengePoint.EventNumber)
			{
				// the latest scavengepoint is suitable
				logger.Debug(
					"SCAVENGING: Using existing scavenge point {scavengePointNumber}",
					latestScavengePoint.EventNumber);
				nextScavengePoint = latestScavengePoint;
			}
			else
			{
				if (syncOnly)
				{
					logger.Debug("SCAVENGING: No existing scavenge point to sync with, nothing to do.");
					return;
				}
				else
				{
					// the latest scavengepoint is the prev scavenge point, so create a new one
					var expectedVersion = prevScavengePoint.EventNumber;
					logger.Debug(
						"SCAVENGING: Creating the next scavenge point: {scavengePointNumber}",
						expectedVersion + 1);

					nextScavengePoint = await scavengePointSource
						.AddScavengePointAsync(
							expectedVersion,
							threshold: thresholdForNewScavenge,
							cancellationToken);
				}
			}
		}

		// we now have a nextScavengePoint.
		await Time(stopwatch, "Accumulation", cancellationToken =>
				accumulator.Accumulate(prevScavengePoint, nextScavengePoint, state, cancellationToken)
			, cancellationToken);
		await AfterAccumulation(nextScavengePoint, scavengerLogger, stopwatch, cancellationToken);
	}

	private async ValueTask AfterAccumulation(
		ScavengePoint scavengepoint,
		ITFChunkScavengerLog scavengerLogger,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		LogCollisions();
		await Time(stopwatch, "Calculation", cancellationToken =>
		{
			calculator.Calculate(scavengepoint, state, cancellationToken);
			return ValueTask.CompletedTask;
		}, cancellationToken);
		await AfterCalculation(scavengepoint, scavengerLogger, stopwatch, cancellationToken);
	}

	private async ValueTask AfterCalculation(
		ScavengePoint scavengePoint,
		ITFChunkScavengerLog scavengerLogger,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		await Time(stopwatch, "Chunk execution", cancellationToken =>
			chunkExecutor.Execute(scavengePoint, state, scavengerLogger, cancellationToken), cancellationToken);
		await AfterChunkExecution(scavengePoint, scavengerLogger, stopwatch, cancellationToken);
	}

	private async ValueTask AfterChunkExecution(
		ScavengePoint scavengePoint,
		ITFChunkScavengerLog scavengerLogger,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		await Time(stopwatch, "Chunk merging", cancellationToken =>
			chunkMerger.MergeChunks(scavengePoint, state, scavengerLogger, cancellationToken), cancellationToken);
		await AfterChunkMerging(scavengePoint, scavengerLogger, stopwatch, cancellationToken);
	}

	private async ValueTask AfterChunkMerging(
		ScavengePoint scavengePoint,
		ITFChunkScavengerLog scavengerLogger,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		await Time(stopwatch, "Index execution", cancellationToken =>
		{
			indexExecutor.Execute(scavengePoint, state, scavengerLogger, cancellationToken);
			return ValueTask.CompletedTask;
		}, cancellationToken);
		await AfterIndexExecution(scavengePoint, stopwatch, cancellationToken);
	}

	private async ValueTask AfterIndexExecution(
		ScavengePoint scavengePoint,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{

		await Time(stopwatch, "Cleaning", cancellationToken =>
		{
			cleaner.Clean(scavengePoint, state, cancellationToken);
			return ValueTask.CompletedTask;
		}, cancellationToken);
		AfterCleaning(scavengePoint);
	}

	private void AfterCleaning(ScavengePoint scavengePoint)
	{
		state.SetCheckpoint(new ScavengeCheckpoint.Done(scavengePoint));
	}
}
