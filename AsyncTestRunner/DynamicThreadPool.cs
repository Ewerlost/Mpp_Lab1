#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicThreadPoolModule
{
    public sealed record PoolSnapshot(
        int MinWorkers,
        int MaxWorkers,
        int TotalWorkers,
        int BusyWorkers,
        int IdleWorkers,
        int QueuedItems);

    public sealed class PoolHandle<T>
    {
        private readonly TaskCompletionSource<T> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name { get; }

        internal PoolHandle(string name)
        {
            Name = name;
        }

        public Task<T> Completion => _tcs.Task;

        internal void SetResult(T value) => _tcs.TrySetResult(value);
        internal void SetException(Exception ex) => _tcs.TrySetException(ex);
    }

    public sealed class DynamicThreadPool<T> : IDisposable
    {
        private sealed class QueuedItem
        {
            public required Func<T> Work { get; init; }
            public required PoolHandle<T> Handle { get; init; }
            public required string Name { get; init; }
            public int Priority { get; init; }
            public long Sequence { get; init; }
            public DateTime EnqueuedAtUtc { get; init; }
        }

        private sealed class WorkerState
        {
            public int Id { get; init; }
            public Thread? Thread { get; set; }
            public bool Busy { get; set; }
            public bool ReplacementRequested { get; set; }
            public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
            public DateTime? CurrentStartUtc { get; set; }
            public QueuedItem? CurrentItem { get; set; }
        }

        private readonly object _sync = new();
        private readonly PriorityQueue<QueuedItem, (int Priority, long Sequence)> _queue = new();
        private readonly List<WorkerState> _workers = new();
        private readonly Thread _supervisorThread;
        private readonly CancellationTokenSource _cts = new();
        private long _sequence;
        private int _workerIdSeed;
        private bool _stopping;

        public int MinWorkers { get; }
        public int MaxWorkers { get; }
        public TimeSpan IdleTimeout { get; }
        public TimeSpan QueuePressureTimeout { get; }
        public TimeSpan HungWorkerTimeout { get; }
        public TimeSpan SupervisorInterval { get; }

        public event Action<PoolSnapshot>? StateChanged;
        public event Action<string>? Log;

        public DynamicThreadPool(
            int minWorkers,
            int maxWorkers,
            TimeSpan idleTimeout,
            TimeSpan queuePressureTimeout,
            TimeSpan hungWorkerTimeout,
            TimeSpan supervisorInterval)
        {
            if (minWorkers < 1) throw new ArgumentOutOfRangeException(nameof(minWorkers));
            if (maxWorkers < minWorkers) throw new ArgumentOutOfRangeException(nameof(maxWorkers));
            if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            if (queuePressureTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(queuePressureTimeout));
            if (hungWorkerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(hungWorkerTimeout));
            if (supervisorInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(supervisorInterval));

            MinWorkers = minWorkers;
            MaxWorkers = maxWorkers;
            IdleTimeout = idleTimeout;
            QueuePressureTimeout = queuePressureTimeout;
            HungWorkerTimeout = hungWorkerTimeout;
            SupervisorInterval = supervisorInterval;

            lock (_sync)
            {
                EnsureMinWorkersLocked();
            }

            _supervisorThread = new Thread(SupervisorLoop)
            {
                IsBackground = true,
                Name = "DynamicThreadPool-Supervisor"
            };
            _supervisorThread.Start();
        }

        public PoolHandle<T> Enqueue(Func<T> work, int priority = 0, string? name = null)
        {
            if (work is null) throw new ArgumentNullException(nameof(work));

            var handle = new PoolHandle<T>(name ?? "work-item");
            PoolSnapshot? snapshot = null;

            lock (_sync)
            {
                if (_stopping)
                    throw new ObjectDisposedException(nameof(DynamicThreadPool<T>));

                var item = new QueuedItem
                {
                    Work = work,
                    Handle = handle,
                    Name = name ?? $"work-{_sequence + 1}",
                    Priority = priority,
                    Sequence = ++_sequence,
                    EnqueuedAtUtc = DateTime.UtcNow
                };

                _queue.Enqueue(item, (-item.Priority, item.Sequence));

                if (_workers.Count < MaxWorkers)
                {
                    var oldestWait = GetOldestQueueWaitLocked();
                    if (_queue.Count > _workers.Count || oldestWait >= QueuePressureTimeout)
                    {
                        SpawnWorkerLocked(reason: "queue pressure");
                    }
                }

                Monitor.PulseAll(_sync);
                snapshot = CaptureSnapshotLocked();
            }

            StateChanged?.Invoke(snapshot!);
            return handle;
        }

        public PoolSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return CaptureSnapshotLocked();
            }
        }

        public void Stop(bool waitForDrain = true)
        {
            lock (_sync)
            {
                if (_stopping) return;
                _stopping = true;
                Monitor.PulseAll(_sync);
            }

            _cts.Cancel();

            if (waitForDrain)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);

                while (DateTime.UtcNow < deadline)
                {
                    lock (_sync)
                    {
                        if (_workers.Count == 0)
                            break;
                    }

                    Thread.Sleep(50);
                }
            }
        }

        public void Dispose()
        {
            Stop(waitForDrain: true);
            _cts.Dispose();
        }

        private void SupervisorLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(SupervisorInterval);

                    List<string> messages = new();
                    PoolSnapshot? snapshot = null;

                    lock (_sync)
                    {
                        if (_stopping)
                            break;

                        EnsureMinWorkersLocked();

                        if (_queue.Count > 0 && _workers.Count < MaxWorkers)
                        {
                            var oldestWait = GetOldestQueueWaitLocked();
                            if (oldestWait >= QueuePressureTimeout || _queue.Count > _workers.Count)
                            {
                                SpawnWorkerLocked("supervisor scale-up");
                                snapshot = CaptureSnapshotLocked();
                            }
                        }

                        var now = DateTime.UtcNow;

                        foreach (var worker in _workers.ToList())
                        {
                            if (!worker.Busy || worker.ReplacementRequested || worker.CurrentStartUtc is null)
                                continue;

                            var runningFor = now - worker.CurrentStartUtc.Value;

                            if (runningFor >= HungWorkerTimeout)
                            {
                                worker.ReplacementRequested = true;
                                messages.Add(
                                    $"Worker #{worker.Id} looks hung ({runningFor.TotalMilliseconds:n0} ms). " +
                                    $"A replacement worker will be started.");

                                if (_workers.Count < MaxWorkers)
                                {
                                    SpawnWorkerLocked("hung replacement");
                                    snapshot = CaptureSnapshotLocked();
                                }
                            }
                        }
                    }

                    foreach (var msg in messages)
                        Log?.Invoke(msg);

                    if (snapshot is not null)
                        StateChanged?.Invoke(snapshot);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Supervisor stopped with error: {ex.GetBaseException().Message}");
            }
        }

        private void WorkerLoop(WorkerState worker)
        {
            Log?.Invoke($"Worker #{worker.Id} started on managed thread {Thread.CurrentThread.ManagedThreadId}.");

            try
            {
                while (true)
                {
                    QueuedItem? item = null;
                    bool shouldExitIdle = false;

                    lock (_sync)
                    {
                        while (!_stopping && _queue.Count == 0 && !worker.ReplacementRequested)
                        {
                            var idleFor = DateTime.UtcNow - worker.LastActiveUtc;

                            if (_workers.Count > MinWorkers && idleFor >= IdleTimeout)
                            {
                                shouldExitIdle = true;
                                break;
                            }

                            Monitor.Wait(_sync, 200);
                        }

                        if (_stopping && _queue.Count == 0)
                            break;

                        if (shouldExitIdle)
                            break;

                        if (worker.ReplacementRequested && !worker.Busy && _queue.Count == 0)
                            break;

                        if (_queue.Count == 0)
                            continue;

                        _queue.TryDequeue(out item, out _);

                        worker.Busy = true;
                        worker.CurrentItem = item;
                        worker.CurrentStartUtc = DateTime.UtcNow;
                        worker.LastActiveUtc = DateTime.UtcNow;
                    }

                    if (item is null)
                        continue;

                    try
                    {
                        var result = item.Work();
                        item.Handle.SetResult(result);
                    }
                    catch (Exception ex)
                    {
                        item.Handle.SetException(ex);
                        Log?.Invoke(
                            $"Worker #{worker.Id} failed item '{item.Name}': {ex.GetBaseException().Message}");
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            worker.Busy = false;
                            worker.CurrentItem = null;
                            worker.CurrentStartUtc = null;
                            worker.LastActiveUtc = DateTime.UtcNow;
                            Monitor.PulseAll(_sync);
                        }

                        StateChanged?.Invoke(GetSnapshot());
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Worker #{worker.Id} stopped with error: {ex.GetBaseException().Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _workers.Remove(worker);
                    Monitor.PulseAll(_sync);
                }

                StateChanged?.Invoke(GetSnapshot());
                Log?.Invoke($"Worker #{worker.Id} stopped.");
            }
        }

        private void EnsureMinWorkersLocked()
        {
            while (_workers.Count < MinWorkers && _workers.Count < MaxWorkers)
            {
                SpawnWorkerLocked("minimum workers");
            }
        }

        private void SpawnWorkerLocked(string reason)
        {
            if (_stopping || _workers.Count >= MaxWorkers)
                return;

            var worker = new WorkerState
            {
                Id = Interlocked.Increment(ref _workerIdSeed),
                LastActiveUtc = DateTime.UtcNow
            };

            var thread = new Thread(() => WorkerLoop(worker))
            {
                IsBackground = true,
                Name = $"DynamicThreadPool-Worker-{worker.Id}"
            };

            worker.Thread = thread;
            _workers.Add(worker);
            thread.Start();

            Log?.Invoke($"Spawned worker #{worker.Id} because of {reason}. Total workers: {_workers.Count}.");
        }

        private TimeSpan GetOldestQueueWaitLocked()
        {
            if (_queue.Count == 0)
                return TimeSpan.Zero;

            DateTime oldest = DateTime.UtcNow;

            foreach (var entry in _queue.UnorderedItems)
            {
                if (entry.Element.EnqueuedAtUtc < oldest)
                    oldest = entry.Element.EnqueuedAtUtc;
            }

            return DateTime.UtcNow - oldest;
        }

        private PoolSnapshot CaptureSnapshotLocked()
        {
            var total = _workers.Count;
            var busy = _workers.Count(w => w.Busy);
            var idle = total - busy;

            return new PoolSnapshot(
                MinWorkers,
                MaxWorkers,
                total,
                busy,
                idle,
                _queue.Count);
        }
    }
}