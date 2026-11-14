using System.Runtime.ExceptionServices;

namespace OpenJpeg.Internal;

/// <summary>
/// Stands in for upstream's opj_thread_pool. Work runs on a small set of worker threads (<see cref="WorkerPool"/>)
/// and on the calling thread.
/// </summary>
internal static class Parallelism
{
    /// <summary>Number of worker threads to use, upstream's opj_thread_pool_get_thread_count.</summary>
    internal static int ThreadCount(bool disableMultiThreading) =>
        disableMultiThreading ? 1 : Environment.ProcessorCount;

    /// <summary>
    /// Runs <paramref name="work"/> on every job and waits for all of them.
    /// An exception in a job is rethrown here, on the calling thread. (Before, jobs ran as raw thread-pool
    /// work items, where an exception from a corrupt file terminated the whole process.)
    /// </summary>
    internal static void RunAll<T>(IReadOnlyList<T> jobs, Action<T> work)
    {
        if (jobs.Count == 1)
        {
            work(jobs[0]);
            return;
        }

        Run(jobs, jobs.Count, work, null);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on each job produced by <paramref name="jobs"/>, on up to
    /// <paramref name="maxThreads"/> threads, and stops early once <paramref name="keepGoing"/> returns false.
    /// The generator is enumerated by the calling thread, before any job runs. With one thread everything runs
    /// inline, like upstream with an empty thread pool.
    /// </summary>
    internal static void RunAll<T>(IEnumerable<T> jobs, int maxThreads, Action<T> work, Func<bool> keepGoing)
    {
        if (maxThreads <= 1)
        {
            foreach (var job in jobs)
            {
                work(job);
                if (!keepGoing()) break;
            }
            return;
        }

        Run(new List<T>(jobs), maxThreads, work, keepGoing);
    }

    /// <summary>
    /// Splits <c>0 .. count</c> into at most <paramref name="maxThreads"/> ranges of at least
    /// <paramref name="minPerRange"/> items (a multiple of 64, so ranges start on a cache line) and runs
    /// <paramref name="work"/> on each. A single range runs inline. For loops whose items are independent.
    /// </summary>
    internal static void RunRanges(int count, int minPerRange, int maxThreads, Action<int, int> work)
    {
        int ranges = Math.Min(maxThreads, count / minPerRange);
        if (ranges <= 1)
        {
            work(0, count);
            return;
        }

        int step = (count / ranges + 63) & ~63;
        var jobs = new Range[(count + step - 1) / step];
        for (int i = 0; i < jobs.Length; i++)
        {
            jobs[i] = new Range(i * step, Math.Min(count, (i + 1) * step));
        }

        RunAll(jobs, r => work(r.Start, r.End));
    }

    private readonly struct Range
    {
        internal readonly int Start, End;
        internal Range(int start, int end) { Start = start; End = end; }
    }

    private static void Run<T>(IReadOnlyList<T> jobs, int maxThreads, Action<T> work, Func<bool>? keepGoing)
    {
        var batch = new Batch<T>(jobs, work, keepGoing);
        WorkerPool.Dispatch(batch, Math.Min(maxThreads, jobs.Count) - 1);
        batch.Run();
        batch.WaitDone();
        if (batch.Error is not null)
        {
            ExceptionDispatchInfo.Capture(batch.Error).Throw();
        }
    }

    /// <summary>A set of jobs that the calling thread and the workers take from one counter.</summary>
    private abstract class Batch
    {
        internal abstract void Run();
    }

    private sealed class Batch<T> : Batch
    {
        private readonly IReadOnlyList<T> _jobs;
        private readonly Action<T> _work;
        private readonly Func<bool>? _keepGoing;
        private int _next = -1;
        private int _done;
        internal Exception? Error;

        internal Batch(IReadOnlyList<T> jobs, Action<T> work, Func<bool>? keepGoing)
        {
            _jobs = jobs;
            _work = work;
            _keepGoing = keepGoing;
        }

        /// <summary>Takes and runs jobs until none are left. A job after a failure is counted but not run.</summary>
        internal override void Run()
        {
            int i;
            while ((i = Interlocked.Increment(ref _next)) < _jobs.Count)
            {
                try
                {
                    if (Volatile.Read(ref Error) is null && (_keepGoing is null || _keepGoing()))
                    {
                        _work(_jobs[i]);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref Error, e, null);
                }
                finally
                {
                    Interlocked.Increment(ref _done);
                }
            }
        }

        /// <summary>
        /// Waits until every job has finished. A worker may still hold the last jobs, each at most a code-block
        /// or a few rows, so this spins and then yields; it never sleeps, because <c>Sleep(1)</c> (what
        /// <c>SpinWait</c> falls back to) can last a whole timer tick: 3 to 9 ms per tile on 10719.
        /// </summary>
        internal void WaitDone()
        {
            for (int spins = 0; Volatile.Read(ref _done) < _jobs.Count; spins++)
            {
                if (spins < 64)
                {
                    Thread.SpinWait(20);
                }
                else
                {
                    Thread.Yield();
                }
            }
        }
    }

    /// <summary>
    /// Dedicated worker threads. The .NET thread pool wakes its workers one after another (each woken worker
    /// requests the next), so a batch of short jobs, such as the code-blocks of a 256x256 tile, was served by a
    /// few late threads: on 10719 only 11 of 32 threads ran jobs and the average one started 0.4 ms in. Here
    /// the caller signals every worker directly. Workers do not spin: spinning made the other stages slower
    /// (they compete for cores) by more than it saved.
    /// A worker is idle (<c>null</c>), has a batch, or is retired (no thread). A thread that has been idle for
    /// <see cref="IdleTimeoutMs"/> exits, so the threads and their cached T1 contexts do not outlive the work.
    /// A batch finds no idle worker when other decodes use them all: its caller then runs the jobs alone.
    /// </summary>
    private static class WorkerPool
    {
        private const int IdleTimeoutMs = 10_000;

        private sealed class Worker
        {
            internal Batch? Batch = Retired;
            internal readonly ManualResetEventSlim Wake = new ManualResetEventSlim(false, 0);
        }

        private static readonly Batch Retired = new RetiredBatch();
        private static readonly Worker[] Workers = CreateWorkers();

        private sealed class RetiredBatch : Batch
        {
            internal override void Run() { }
        }

        private static Worker[] CreateWorkers()
        {
            var workers = new Worker[Math.Max(Environment.ProcessorCount - 1, 0)];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Worker();
            }
            return workers;
        }

        /// <summary>Hands <paramref name="batch"/> to up to <paramref name="wanted"/> workers that are not busy.</summary>
        internal static void Dispatch(Batch batch, int wanted)
        {
            foreach (var worker in Workers)
            {
                if (wanted <= 0)
                {
                    return;
                }

                var current = Volatile.Read(ref worker.Batch);
                if (current is null)
                {
                    if (Interlocked.CompareExchange(ref worker.Batch, batch, null) is null)
                    {
                        worker.Wake.Set();
                        wanted--;
                    }
                }
                else if (ReferenceEquals(current, Retired))
                {
                    if (ReferenceEquals(Interlocked.CompareExchange(ref worker.Batch, batch, Retired), Retired))
                    {
                        try
                        {
                            new Thread(() => Loop(worker)) { IsBackground = true, Name = "OpenJpeg worker" }.Start();
                            wanted--;
                        }
                        catch (Exception)
                        {
                            // No thread: the caller runs the jobs.
                            Volatile.Write(ref worker.Batch, Retired);
                        }
                    }
                }
            }
        }

        private static void Loop(Worker worker)
        {
            while (true)
            {
                // Assigned before the thread started or before Wake was set.
                Volatile.Read(ref worker.Batch)!.Run();
                Volatile.Write(ref worker.Batch, null);

                if (!worker.Wake.Wait(IdleTimeoutMs))
                {
                    if (Interlocked.CompareExchange(ref worker.Batch, Retired, null) is null)
                    {
                        return;
                    }

                    // A batch arrived just now: its Set is on the way.
                    worker.Wake.Wait();
                }

                worker.Wake.Reset();
            }
        }
    }
}
