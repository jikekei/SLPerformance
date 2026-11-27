using System;
using System.Threading;

namespace SLPerformance.Threading;

/// <summary>
/// Bounded fixed-size worker thread pool for synchronous fork-join CPU-bound parallel chunks.
/// - Zero unbounded queues
/// - Zero per-dispatch allocations
/// - Deterministic exception propagation back to caller thread
/// - Clean shutdown via Dispose()
/// </summary>
public sealed class BoundedWorkerPool : IDisposable
{
    private readonly Thread[] _workers;
    private readonly ManualResetEventSlim[] _startEvents;
    private readonly ManualResetEventSlim _doneEvent = new ManualResetEventSlim(false);

    private Action<int, int, int> _currentWork;
    private int _currentItemCount;
    private int _activeWorkerCount;
    private int _remainingWorkers;
    private Exception _firstException;
    private volatile bool _disposed;

    public int WorkerCount => _workers.Length;

    public BoundedWorkerPool(int workerCount, string namePrefix = "SLPerf-Worker")
    {
        int clamped = Math.Max(1, Math.Min(4, workerCount));
        _workers = new Thread[clamped];
        _startEvents = new ManualResetEventSlim[clamped];

        for (int i = 0; i < clamped; i++)
        {
            int workerIndex = i;
            _startEvents[i] = new ManualResetEventSlim(false);
            Thread t = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"{namePrefix}-{i}",
                Priority = ThreadPriority.AboveNormal
            };
            _workers[i] = t;
            t.Start();
        }
    }

    /// <summary>
    /// Executes work(workerIndex, startInclusive, endExclusive) synchronously across the caller thread
    /// plus up to WorkerCount background threads. Never spawns extra threads or queues work asynchronously.
    /// </summary>
    public void ExecuteParallelFor(int itemCount, Action<int, int, int> chunkAction)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BoundedWorkerPool));
        }

        if (itemCount <= 0 || chunkAction == null)
        {
            return;
        }

        int totalParticipants = Math.Min(itemCount, _workers.Length + 1);
        if (totalParticipants <= 1)
        {
            chunkAction(0, 0, itemCount);
            return;
        }

        int bgWorkers = totalParticipants - 1;
        _currentWork = chunkAction;
        _currentItemCount = itemCount;
        _activeWorkerCount = totalParticipants;
        _firstException = null;
        _doneEvent.Reset();
        Volatile.Write(ref _remainingWorkers, bgWorkers);

        // Wake background workers (indices 1..bgWorkers)
        for (int i = 0; i < bgWorkers; i++)
        {
            _startEvents[i].Set();
        }

        // Caller thread executes slice 0 immediately (avoids wasting main thread idle time)
        int slice0Start = 0;
        int slice0End = (itemCount + totalParticipants - 1) / totalParticipants;
        if (slice0End > itemCount)
        {
            slice0End = itemCount;
        }

        try
        {
            chunkAction(0, slice0Start, slice0End);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _firstException, ex, null);
        }

        // Wait for all background workers to complete their slice
        _doneEvent.Wait();

        _currentWork = null;
        Exception err = _firstException;
        if (err != null)
        {
            throw new AggregateException("One or more errors occurred in BoundedWorkerPool.", err);
        }
    }

    private void WorkerLoop(int workerSlot)
    {
        ManualResetEventSlim startEvent = _startEvents[workerSlot];
        int participantIndex = workerSlot + 1;

        while (true)
        {
            startEvent.Wait();
            if (_disposed)
            {
                return;
            }

            startEvent.Reset();

            Action<int, int, int> work = _currentWork;
            int totalItems = _currentItemCount;
            int totalParticipants = _activeWorkerCount;

            if (work != null && participantIndex < totalParticipants)
            {
                int chunkSize = (totalItems + totalParticipants - 1) / totalParticipants;
                int start = participantIndex * chunkSize;
                int end = start + chunkSize;
                if (end > totalItems)
                {
                    end = totalItems;
                }

                if (start < end)
                {
                    try
                    {
                        work(participantIndex, start, end);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref _firstException, ex, null);
                    }
                }
            }

            if (Interlocked.Decrement(ref _remainingWorkers) == 0)
            {
                _doneEvent.Set();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _startEvents.Length; i++)
        {
            _startEvents[i].Set();
        }

        for (int i = 0; i < _workers.Length; i++)
        {
            if (_workers[i].IsAlive)
            {
                _workers[i].Join(500);
            }

            _startEvents[i].Dispose();
        }

        _doneEvent.Dispose();
    }
}
