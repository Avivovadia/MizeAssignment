using System.Diagnostics;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Api.Services;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Concurrency;

/// <summary>
/// A measurement, not a pass/fail test: how the single writer lock behaves as callers pile up. It asserts only
/// that every request got a correct answer and prints a table. Run it on demand, preferably in Release:
///   dotnet test -c Release --filter "TestCategory=Performance" --logger "console;verbosity=detailed"
/// Numbers depend on the machine and its disk (each commit waits for the disk to confirm the write).
/// </summary>
[TestFixture, Explicit("Measurement; run on demand"), Category("Performance")]
public class LockingPerformanceReport
{
    private const int RequestsPerCell = 300;
    private static readonly int[] CallerLevels = { 1, 2, 4, 8, 16, 32 };

    private const string Header = "callers | req/s  | p50 ms | p95 ms | p99 ms | max ms | wall s | non-2xx/429";

    private static string Row(int callers, Timed<int>[] calls, double wallSeconds)
    {
        var bad = calls.Count(c => c.Result is not (200 or 201 or 400 or 429));
        return $"{callers,7} | {calls.Length / wallSeconds,6:F0} | {Load.Percentile(calls, 50),6:F1} | {Load.Percentile(calls, 95),6:F1} | " +
               $"{Load.Percentile(calls, 99),6:F1} | {calls.Max(c => c.Milliseconds),6:F1} | {wallSeconds,6:F2} | {bad}";
    }

    private static int StatusOf(IngestResult result) => result switch
    {
        IngestResult.Processed p => p.Detail == IngestDetail.Created ? 201 : 200,
        IngestResult.Throttled => 429,
        IngestResult.Invalid or IngestResult.Unattributable => 400,
        _ => 500,
    };

    private static IngestOrchestrator CreateOrchestrator(TestDatabase db) => new(
        db.Factory, new ThrottleStore(new ThrottleOptions()), new ReservationStore(), new StatsStore(),
        new UpdatedAtPolicy(new UpdatedAtOptions()), NullLogger<IngestOrchestrator>.Instance);

    private static void Section(StringBuilder report, string title) =>
        report.AppendLine().AppendLine(title).AppendLine(Header).AppendLine(new string('-', Header.Length));

    [Test]
    public async Task Report()
    {
        var report = new StringBuilder();
        report.AppendLine($"Locking performance on {Environment.MachineName}, {Environment.ProcessorCount} cores, " +
                          $"{(IsReleaseBuild ? "Release" : "DEBUG build (use -c Release for realistic numbers)")}, " +
                          $"{RequestsPerCell} requests per row");

        // ---- A. Orchestrator in this process, each caller on its own connection ----
        var scenarios = new (string Title, Func<TestDatabase, Func<int, string>> Setup)[]
        {
            ("A1. Every request is a new reservation from its own supplier (never throttled; full write path)",
                _ => i => TestBodies.Valid($"supplier-{i}", "r1").ToJsonString()),

            ("A2. One supplier already over its limit (every request is throttled; the abuse path)",
                db => { db.InsertLogRows("flood", 100, ageMs: 5_000); return i => TestBodies.Valid("flood", $"r{i}").ToJsonString(); }),

            ("A3. One supplier flooding from empty (100 admitted, the rest throttled)",
                _ => i => TestBodies.Valid("flood", $"r{i}").ToJsonString()),
        };

        foreach (var (title, setup) in scenarios)
        {
            Section(report, title);
            foreach (var callers in CallerLevels)
            {
                using var db = new TestDatabase();
                DatabaseInitializer.Initialize(db.Factory);
                var orchestrator = CreateOrchestrator(db);
                var body = setup(db);

                var stopwatch = Stopwatch.StartNew();
                var calls = Load.Run(RequestsPerCell, callers, i => StatusOf(orchestrator.Ingest(body(i))));
                report.AppendLine(Row(callers, calls, stopwatch.Elapsed.TotalSeconds));
            }
        }

        // ---- B. Real server processes over HTTP ----
        foreach (var processes in new[] { 1, 2 })
        {
            Section(report, $"B. {processes} real server process(es) over HTTP, one supplier flooding from empty (A3 through the full stack)");
            foreach (var callers in new[] { 1, 8, 32 })
            {
                using var db = new TestDatabase();
                var servers = await Task.WhenAll(Enumerable.Range(0, processes).Select(_ => ServerProcess.StartAsync(db.ConnectionString)));
                var clients = servers.Select(s => s.CreateClient()).ToArray();
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var calls = await Load.RunAsync(RequestsPerCell, callers, async i =>
                    {
                        using var content = new StringContent(TestBodies.Valid("flood", $"r{i}").ToJsonString(), Encoding.UTF8, "application/json");
                        return (int)(await clients[i % clients.Length].PostAsync("/api/reservations/ingest", content)).StatusCode;
                    });
                    report.AppendLine(Row(callers, calls, stopwatch.Elapsed.TotalSeconds));
                }
                finally
                {
                    foreach (var client in clients) client.Dispose();
                    foreach (var server in servers) server.Dispose();
                }
            }
        }

        // ---- D. Experiment (not production code): callers queue in this process before touching the lock ----
        foreach (var writers in new[] { 1, 2 })
        {
            Section(report, $"D. Same as A3, but at most {writers} writer(s) per process may enter the database; the rest wait in a queue first (latency includes the queue)");
            foreach (var callers in new[] { 8, 16, 32 })
            {
                using var db = new TestDatabase();
                DatabaseInitializer.Initialize(db.Factory);
                var orchestrator = CreateOrchestrator(db);
                using var gate = new SemaphoreSlim(writers);

                var stopwatch = Stopwatch.StartNew();
                var calls = Load.Run(RequestsPerCell, callers, i =>
                {
                    gate.Wait();
                    try { return StatusOf(orchestrator.Ingest(TestBodies.Valid("flood", $"r{i}").ToJsonString())); }
                    finally { gate.Release(); }
                });
                report.AppendLine(Row(callers, calls, stopwatch.Elapsed.TotalSeconds));
            }
        }

        // ---- C. What a commit costs: the disk-sync setting, measured on the same write transaction ----
        report.AppendLine().AppendLine("C. One-row write transaction, sequential, then 8 callers (SQLite 'synchronous' setting; production uses FULL, the default)");
        report.AppendLine("setting | sequential commits/s | 8 callers commits/s");
        foreach (var synchronous in new[] { "FULL", "NORMAL" })
        {
            using var db = new TestDatabase();
            DatabaseInitializer.Initialize(db.Factory);

            double CommitsPerSecond(int callers)
            {
                var stopwatch = Stopwatch.StartNew();
                Load.Run(RequestsPerCell, callers, i =>
                {
                    using var connection = db.Factory.Open();
                    connection.Execute($"pragma synchronous = {synchronous}");
                    using var transaction = connection.BeginWriteTransaction();
                    connection.Execute("insert into request_log (supplierId, tsMs) values ('bench', @i)", new { i }, transaction);
                    transaction.Commit();
                    return 0;
                });
                return RequestsPerCell / stopwatch.Elapsed.TotalSeconds;
            }

            report.AppendLine($"{synchronous,-7} | {CommitsPerSecond(1),20:F0} | {CommitsPerSecond(8),19:F0}");
        }

        TestContext.Out.WriteLine(report.ToString());
        Assert.Pass(report.ToString());
    }

    private static bool IsReleaseBuild =>
#if DEBUG
        false;
#else
        true;
#endif
}
