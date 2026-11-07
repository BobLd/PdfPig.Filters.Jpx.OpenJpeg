using System.Runtime.ExceptionServices;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Stands in for upstream's opj_thread_pool. Work runs on the .NET thread pool and on the calling thread.
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

            try
            {
                Parallel.For(0, jobs.Count, i => work(jobs[i]));
            }
            catch (AggregateException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                throw;
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on each job produced by <paramref name="jobs"/>, on up to
        /// <paramref name="maxThreads"/> threads, and stops early once <paramref name="keepGoing"/> returns false.
        /// The generator is enumerated by one thread at a time. With one thread everything runs inline,
        /// like upstream with an empty thread pool.
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

            try
            {
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, (job, state) =>
                {
                    work(job);
                    if (!keepGoing()) state.Stop();
                });
            }
            catch (AggregateException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                throw;
            }
        }
    }
}
