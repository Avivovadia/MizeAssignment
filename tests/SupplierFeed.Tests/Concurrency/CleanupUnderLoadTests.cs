using Microsoft.Extensions.Logging.Abstractions;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Api.Services;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Concurrency;

[TestFixture]
public class CleanupUnderLoadTests
{
    private const int Limit = 100;
    private const int Requests = 150;

    [Test, Repeat(3)]
    public void Cleaning_continuously_while_requests_arrive_never_lets_the_limit_be_exceeded_or_undercut()
    {
        using var db = new TestDatabase();
        DatabaseInitializer.Initialize(db.Factory);
        var orchestrator = new IngestOrchestrator(
            db.Factory, new ThrottleStore(new ThrottleOptions()), new ReservationStore(), new StatsStore(),
            new UpdatedAtPolicy(new UpdatedAtOptions()), NullLogger<IngestOrchestrator>.Instance);
        var cleaner = new RequestLogCleaner(db.Factory, new ThrottleOptions(), new CleanupOptions { BatchSize = 200 });

        // A large backlog of expired rows for other suppliers, so every pass really deletes something
        // while requests are in flight.
        foreach (var supplier in new[] { "old-a", "old-b", "old-c" })
            db.InsertLogRows(supplier, 1_500, ageMs: 120_000);

        using var stop = new CancellationTokenSource();
        long deletedWhileRunning = 0;
        var cleanerThread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
                Interlocked.Add(ref deletedWhileRunning, cleaner.Run());
        });
        cleanerThread.Start();

        try
        {
            var calls = Load.Run(Requests, 16, i => orchestrator.Ingest(TestBodies.Valid("s1", $"r{i}").ToJsonString()));

            Assert.That(calls.Count(c => c.Result is IngestResult.Processed), Is.EqualTo(Limit),
                "cleaning must not make the throttle forget in-window requests");
            Assert.That(calls.Count(c => c.Result is IngestResult.Throttled), Is.EqualTo(Requests - Limit));
            Assert.That(calls.Count(c => c.Result is IngestResult.Failed), Is.Zero);
        }
        finally
        {
            stop.Cancel();
            cleanerThread.Join();
        }

        cleaner.Run(); // a final pass, in case the cleaner thread was stopped mid-backlog
        Assert.That(db.LogCount("s1"), Is.EqualTo(Limit), "the in-window rows were all kept");
        Assert.That(db.LogCount("old-a") + db.LogCount("old-b") + db.LogCount("old-c"), Is.Zero, "the expired backlog is gone");
    }
}
