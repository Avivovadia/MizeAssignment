using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Api.Services;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Concurrency;

/// <summary>
/// The orchestrator under real parallel load: many callers in flight at once, each on its own connection,
/// all against one database file. Any exception (for example SQLITE_BUSY) fails the test.
/// </summary>
[TestFixture]
public class IngestConcurrencyTests
{
    private const int Callers = 16;
    private const int Requests = 150;
    private const int Limit = 100;

    private TestDatabase _db = null!;
    private IngestOrchestrator _orchestrator = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
        _orchestrator = new IngestOrchestrator(
            _db.Factory,
            new ThrottleStore(new ThrottleOptions()),
            new ReservationStore(),
            new StatsStore(),
            new UpdatedAtPolicy(new UpdatedAtOptions()),
            NullLogger<IngestOrchestrator>.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private SupplierStats Stats(string supplierId)
    {
        using var connection = _db.Factory.Open();
        return new StatsStore().Get(connection, supplierId);
    }

    private static int Count<T>(IEnumerable<Timed<IngestResult>> calls, Func<T, bool>? where = null) where T : IngestResult =>
        calls.Select(c => c.Result).OfType<T>().Count(r => where?.Invoke(r) ?? true);

    [Test, Repeat(3)]
    public void One_supplier_sending_150_requests_at_once_is_admitted_exactly_up_to_the_limit()
    {
        var calls = Load.Run(Requests, Callers, i => _orchestrator.Ingest(TestBodies.Valid("s1", $"r{i}").ToJsonString()));

        Assert.That(Count<IngestResult.Processed>(calls), Is.EqualTo(Limit));
        Assert.That(Count<IngestResult.Throttled>(calls), Is.EqualTo(Requests - Limit));
        Assert.That(Count<IngestResult.Failed>(calls), Is.Zero);
        Assert.That(_db.Count("reservations"), Is.EqualTo(Limit));
        Assert.That(_db.Count("request_log"), Is.EqualTo(Limit), "never more than the limit in the window");
        Assert.That(Stats("s1"), Is.EqualTo(new SupplierStats("s1", Ingested: Limit, Ignored: 0, Invalid: 0, Throttled: Requests - Limit)));
    }

    [Test, Repeat(3)]
    public void Suppliers_are_limited_independently_while_competing_for_the_same_database()
    {
        var suppliers = new[] { "a", "b", "c" };
        const int perSupplier = 120;

        var calls = Load.Run(suppliers.Length * perSupplier, Callers, i =>
        {
            var supplier = suppliers[i % suppliers.Length];
            return _orchestrator.Ingest(TestBodies.Valid(supplier, $"r{i}").ToJsonString());
        });

        Assert.That(Count<IngestResult.Failed>(calls), Is.Zero);
        foreach (var supplier in suppliers)
        {
            Assert.That(_db.LogCount(supplier), Is.EqualTo(Limit), supplier);
            Assert.That(Stats(supplier), Is.EqualTo(new SupplierStats(supplier, Limit, 0, 0, perSupplier - Limit)), supplier);
        }
    }

    [Test, Repeat(3)]
    public void Concurrent_versions_of_one_reservation_never_regress_the_stored_data()
    {
        const int versions = 50;
        var order = Enumerable.Range(1, versions).OrderBy(_ => Random.Shared.Next()).ToArray();
        var baseTime = DateTimeOffset.Parse(TestBodies.DefaultUpdatedAt);

        var calls = Load.Run(versions, Callers, i =>
        {
            var version = order[i];
            var body = TestBodies.Valid("s1", "r1", updatedAt: baseTime.AddMinutes(version).ToString("O"));
            body["roomId"] = $"room-{version}";
            return _orchestrator.Ingest(body.ToJsonString());
        });

        Assert.That(Count<IngestResult.Processed>(calls), Is.EqualTo(versions), "below the limit: nothing throttled or failed");
        Assert.That(Count<IngestResult.Processed>(calls, p => p.Detail == IngestDetail.Created), Is.EqualTo(1), "created exactly once");

        using var connection = _db.Factory.Open();
        var stored = connection.QuerySingle<(string RoomId, long UpdatedAtMs)>("select roomId, updatedAtMs from reservations");
        Assert.That(stored.RoomId, Is.EqualTo($"room-{versions}"), "the newest version wins, whatever order they ran in");
        Assert.That(stored.UpdatedAtMs, Is.EqualTo(baseTime.AddMinutes(versions).ToUnixTimeMilliseconds()));
        Assert.That(_db.Count("reservations"), Is.EqualTo(1));

        var stats = Stats("s1");
        Assert.That(stats.Ingested + stats.Ignored, Is.EqualTo(versions), "every request was counted exactly once");
    }

    [Test, Repeat(3)]
    public void Valid_and_invalid_requests_mixed_under_load_keep_every_counter_consistent()
    {
        // Every third request is invalid. 150 requests against a limit of 100: 100 admitted, 50 throttled.
        var calls = Load.Run(Requests, Callers, i => _orchestrator.Ingest(
            (i % 3 == 0 ? TestBodies.Invalid("s1") : TestBodies.Valid("s1", $"r{i}")).ToJsonString()));

        var throttled = Count<IngestResult.Throttled>(calls);
        var admittedValid = Count<IngestResult.Processed>(calls);
        var admittedInvalid = Count<IngestResult.Invalid>(calls);
        var invalidSent = Enumerable.Range(0, Requests).Count(i => i % 3 == 0);

        Assert.That(Count<IngestResult.Failed>(calls), Is.Zero);
        Assert.That(throttled, Is.EqualTo(Requests - Limit));
        Assert.That(admittedValid + admittedInvalid, Is.EqualTo(Limit));
        Assert.That(_db.Count("request_log"), Is.EqualTo(Limit));
        Assert.That(_db.Count("reservations"), Is.EqualTo(admittedValid));

        var stats = Stats("s1");
        Assert.That(stats.Ingested, Is.EqualTo(admittedValid));
        Assert.That(stats.Throttled, Is.EqualTo(throttled));
        Assert.That(stats.Invalid, Is.EqualTo(invalidSent), "invalid counts every bad payload, throttled or not");
    }
}
