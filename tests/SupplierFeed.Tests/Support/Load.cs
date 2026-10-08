using System.Diagnostics;

namespace SupplierFeed.Tests.Support;

/// <summary>One finished call: what it returned and how long it took.</summary>
public readonly record struct Timed<T>(T Result, double Milliseconds);

/// <summary>Runs work with a fixed number of callers truly in flight at once, and times each call.</summary>
public static class Load
{
    /// <summary>
    /// Dedicated threads, not the thread pool: the pool grows slowly when threads block on a lock, which
    /// would quietly lower the contention we are trying to create.
    /// </summary>
    public static Timed<T>[] Run<T>(int count, int degree, Func<int, T> work)
    {
        var results = new Timed<T>[count];
        var next = -1;
        var failures = new List<Exception>();
        using var start = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, degree).Select(_ => new Thread(() =>
        {
            start.Wait();
            int i;
            while ((i = Interlocked.Increment(ref next)) < count)
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var result = work(i);
                    results[i] = new Timed<T>(result, stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception exception)
                {
                    lock (failures) failures.Add(exception);
                }
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} of {count} calls threw", failures);

        return results;
    }

    /// <summary>Async version for HTTP: <paramref name="degree"/> requests outstanding at any moment.</summary>
    public static async Task<Timed<T>[]> RunAsync<T>(int count, int degree, Func<int, Task<T>> work)
    {
        var results = new Timed<T>[count];
        using var gate = new SemaphoreSlim(degree);

        await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await work(i);
                results[i] = new Timed<T>(result, stopwatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                gate.Release();
            }
        }));

        return results;
    }

    /// <summary>Nearest-rank percentile of the call durations.</summary>
    public static double Percentile<T>(IEnumerable<Timed<T>> calls, double percentile)
    {
        var sorted = calls.Select(c => c.Milliseconds).OrderBy(ms => ms).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length);
        return sorted[Math.Max(0, rank - 1)];
    }
}
