using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Api.Services;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Services;

[TestFixture]
public class IngestOrchestratorTests
{
    private TestDatabase _db = null!;
    private IngestOrchestrator _orchestrator = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
        _orchestrator = Create(new ThrottleOptions(), new UpdatedAtOptions());
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private IngestOrchestrator Create(ThrottleOptions throttle, UpdatedAtOptions updatedAt) => new(
        _db.Factory,
        new ThrottleStore(throttle),
        new ReservationStore(),
        new StatsStore(),
        new UpdatedAtPolicy(updatedAt),
        NullLogger<IngestOrchestrator>.Instance);

    private IngestResult Ingest(JsonObject body) => _orchestrator.Ingest(body.ToJsonString());

    private SupplierStats Stats(string supplierId = "s1")
    {
        using var connection = _db.Factory.Open();
        return new StatsStore().Get(connection, supplierId);
    }

    private long Count(string table)
    {
        using var connection = _db.Factory.Open();
        return connection.ExecuteScalar<long>($"select count(*) from {table}");
    }

    private void Exec(string sql)
    {
        using var connection = _db.Factory.Open();
        connection.Execute(sql);
    }

    private static SupplierStats Counters(long ingested = 0, long ignored = 0, long invalid = 0, long throttled = 0, string supplierId = "s1") =>
        new(supplierId, ingested, ignored, invalid, throttled);

    private static T AssertResult<T>(IngestResult result) where T : IngestResult
    {
        Assert.That(result, Is.InstanceOf<T>());
        return (T)result;
    }

    // ---- unattributable: no usable supplierId, so nothing is throttled, counted or written ----

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{\"supplierId\":\"  \"}")]
    public void Body_without_a_usable_supplier_is_unattributable_and_touches_no_table(string body)
    {
        var result = AssertResult<IngestResult.Unattributable>(_orchestrator.Ingest(body));

        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0], Does.Contain("supplierId"));
        Assert.That(Count("request_log"), Is.Zero);
        Assert.That(Count("reservations"), Is.Zero);
        Assert.That(Count("supplier_stats"), Is.Zero);
    }

    // ---- every reservation outcome: result, rows and counters ----

    [Test]
    public void New_reservation_is_created_and_counted_as_ingested()
    {
        var result = Ingest(TestBodies.Valid());

        Assert.That(result, Is.EqualTo(new IngestResult.Processed(IngestDetail.Created)));
        Assert.That(((IngestResult.Processed)result).Status, Is.EqualTo(IngestStatus.Ingested));
        Assert.That(Count("reservations"), Is.EqualTo(1));
        Assert.That(Count("request_log"), Is.EqualTo(1));
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 1)));
    }

    [Test]
    public void Changed_details_with_a_newer_version_are_updated_and_counted_as_ingested()
    {
        Ingest(TestBodies.Valid());
        var changed = TestBodies.Valid(updatedAt: TestBodies.LaterUpdatedAt);
        changed["roomId"] = "room-9";

        var result = Ingest(changed);

        Assert.That(result, Is.EqualTo(new IngestResult.Processed(IngestDetail.Updated)));
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 2)));
    }

    [Test]
    public void Resent_reservation_is_a_duplicate_and_counted_as_ignored()
    {
        Ingest(TestBodies.Valid());

        var result = Ingest(TestBodies.Valid());

        Assert.That(result, Is.EqualTo(new IngestResult.Processed(IngestDetail.Duplicate)));
        Assert.That(((IngestResult.Processed)result).Status, Is.EqualTo(IngestStatus.Ignored));
        Assert.That(Count("reservations"), Is.EqualTo(1));
        Assert.That(Count("request_log"), Is.EqualTo(2), "duplicates still count toward the limit");
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 1, ignored: 1)));
    }

    [Test]
    public void Changed_details_with_an_older_version_are_out_of_date_and_counted_as_ignored()
    {
        Ingest(TestBodies.Valid(updatedAt: TestBodies.LaterUpdatedAt));
        var delayed = TestBodies.Valid(updatedAt: TestBodies.EarlierUpdatedAt);
        delayed["roomId"] = "room-9";

        var result = Ingest(delayed);

        Assert.That(result, Is.EqualTo(new IngestResult.Processed(IngestDetail.OutOfDate)));
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 1, ignored: 1)));
        using var connection = _db.Factory.Open();
        Assert.That(connection.ExecuteScalar<string>("select roomId from reservations"), Is.EqualTo("room-1"));
    }

    // ---- invalid: admitted and counted toward the limit, but never written to reservations ----

    [Test]
    public void Invalid_payload_counts_toward_the_limit_and_as_invalid_but_writes_no_reservation()
    {
        var result = AssertResult<IngestResult.Invalid>(Ingest(TestBodies.Invalid()));

        Assert.That(result.Errors.Single(), Does.Contain("price"));
        Assert.That(Count("request_log"), Is.EqualTo(1));
        Assert.That(Count("reservations"), Is.Zero);
        Assert.That(Stats(), Is.EqualTo(Counters(invalid: 1)));
    }

    [Test]
    public void Far_future_updated_at_is_invalid_with_the_policy_message_and_counts_toward_the_limit()
    {
        var body = TestBodies.Valid(updatedAt: DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"));

        var result = AssertResult<IngestResult.Invalid>(Ingest(body));

        Assert.That(result.Errors.Single(), Does.Contain("updatedAtUtc").And.Contain("300"));
        Assert.That(Count("request_log"), Is.EqualTo(1));
        Assert.That(Count("reservations"), Is.Zero);
        Assert.That(Stats(), Is.EqualTo(Counters(invalid: 1)));
    }

    [Test]
    public void Updated_at_slightly_ahead_but_within_the_allowed_skew_is_accepted()
    {
        var body = TestBodies.Valid(updatedAt: DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"));

        Assert.That(Ingest(body), Is.EqualTo(new IngestResult.Processed(IngestDetail.Created)));
    }

    // ---- throttled ----

    [Test]
    public void Throttled_request_writes_no_reservation_and_no_window_row_and_reports_the_rule()
    {
        _db.InsertLogRows("s1", 100, ageMs: 10_000);

        var result = AssertResult<IngestResult.Throttled>(Ingest(TestBodies.Valid()));

        Assert.That(result.Decision.Limit, Is.EqualTo(100));
        Assert.That(result.Decision.WindowSeconds, Is.EqualTo(60));
        Assert.That(result.Decision.RetryAfterMs, Is.InRange(1, 60_000));
        Assert.That(Count("reservations"), Is.Zero);
        Assert.That(Count("request_log"), Is.EqualTo(100));
        Assert.That(Stats(), Is.EqualTo(Counters(throttled: 1)));
    }

    // Validity and the throttle outcome are two separate facts about a request, and both are recorded so a
    // supplier sending garbage stays visible in `invalid` even while it is being throttled.
    [Test]
    public void Throttled_invalid_payload_is_counted_as_both_invalid_and_throttled()
    {
        _db.InsertLogRows("s1", 100, ageMs: 10_000);

        var result = AssertResult<IngestResult.Throttled>(Ingest(TestBodies.Invalid()));

        Assert.That(result.Decision.Limit, Is.EqualTo(100));
        Assert.That(Stats(), Is.EqualTo(Counters(invalid: 1, throttled: 1)));
        Assert.That(Count("request_log"), Is.EqualTo(100), "a throttled request still adds no window row");
        Assert.That(Count("reservations"), Is.Zero);
    }

    [Test]
    public void Throttled_far_future_payload_is_counted_as_both_invalid_and_throttled()
    {
        _db.InsertLogRows("s1", 100, ageMs: 10_000);
        var body = TestBodies.Valid(updatedAt: DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"));

        AssertResult<IngestResult.Throttled>(Ingest(body));

        Assert.That(Stats(), Is.EqualTo(Counters(invalid: 1, throttled: 1)));
    }

    [Test]
    public void Throttled_valid_payload_is_counted_as_throttled_only()
    {
        _db.InsertLogRows("s1", 100, ageMs: 10_000);

        AssertResult<IngestResult.Throttled>(Ingest(TestBodies.Valid()));

        Assert.That(Stats(), Is.EqualTo(Counters(throttled: 1)));
    }

    [Test]
    public void Invalid_counter_counts_every_invalid_request_whether_or_not_it_was_throttled()
    {
        _db.InsertLogRows("s1", 99, ageMs: 10_000);

        Ingest(TestBodies.Invalid());   // takes the 100th slot: admitted, invalid
        Ingest(TestBodies.Invalid());   // window full: throttled, still invalid
        Ingest(TestBodies.Valid());     // window full: throttled, valid

        Assert.That(Stats(), Is.EqualTo(Counters(invalid: 2, throttled: 2)));
    }

    [Test]
    public void The_101st_request_is_throttled_after_100_were_processed()
    {
        var results = Enumerable.Range(0, 101)
            .Select(i => Ingest(TestBodies.Valid(reservationId: $"r{i}")))
            .ToList();

        Assert.That(results.Take(100), Is.All.EqualTo(new IngestResult.Processed(IngestDetail.Created)));
        Assert.That(results[100], Is.InstanceOf<IngestResult.Throttled>());
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 100, throttled: 1)));
    }

    [Test]
    public void Counters_add_up_to_the_attributable_requests_of_a_mixed_sequence()
    {
        var attributable = new[]
        {
            Ingest(TestBodies.Valid()),                                                    // created
            Ingest(TestBodies.Valid()),                                                    // duplicate
            Ingest(TestBodies.Invalid()),                                                  // invalid
            Ingest(TestBodies.Valid(reservationId: "r2")),                                 // created
            Ingest(TestBodies.Valid(updatedAt: TestBodies.EarlierUpdatedAt)),              // duplicate (older, same details)
        };
        _orchestrator.Ingest("not json");                                                  // unattributable: in no counter

        var stats = Stats();

        Assert.That(attributable, Has.Length.EqualTo(5));
        Assert.That(stats.Ingested + stats.Ignored + stats.Invalid + stats.Throttled, Is.EqualTo(5));
        Assert.That(stats, Is.EqualTo(Counters(ingested: 2, ignored: 2, invalid: 1)));
    }

    [Test]
    public void Suppliers_are_counted_and_throttled_independently()
    {
        _db.InsertLogRows("s1", 100, ageMs: 10_000);

        Assert.That(Ingest(TestBodies.Valid("s1")), Is.InstanceOf<IngestResult.Throttled>());
        Assert.That(Ingest(TestBodies.Valid("s2")), Is.EqualTo(new IngestResult.Processed(IngestDetail.Created)));
        Assert.That(Stats("s2"), Is.EqualTo(Counters(ingested: 1, supplierId: "s2")));
    }

    // ---- failures after admission: the slot is kept, everything else is rolled back ----

    [Test]
    public void Failure_while_writing_the_reservation_keeps_the_quota_slot_and_changes_nothing_else()
    {
        Exec("create trigger fail_reservations before insert on reservations begin select raise(abort, 'boom'); end");

        var result = Ingest(TestBodies.Valid());

        Assert.That(result, Is.InstanceOf<IngestResult.Failed>());
        Assert.That(Count("request_log"), Is.EqualTo(1), "the slot stays consumed so failures cannot be retried for free");
        Assert.That(Count("reservations"), Is.Zero);
        Assert.That(Stats(), Is.EqualTo(Counters()));
    }

    [Test]
    public void Service_recovers_on_the_next_request_after_a_failure()
    {
        Exec("create trigger fail_reservations before insert on reservations begin select raise(abort, 'boom'); end");
        Ingest(TestBodies.Valid());
        Exec("drop trigger fail_reservations");

        var result = Ingest(TestBodies.Valid());

        Assert.That(result, Is.EqualTo(new IngestResult.Processed(IngestDetail.Created)));
        Assert.That(Count("request_log"), Is.EqualTo(2));
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 1)));
    }

    [Test]
    public void Failure_while_counting_rolls_back_the_reservation_change_so_data_and_stats_cannot_drift()
    {
        Ingest(TestBodies.Valid());
        Exec("create trigger fail_stats_insert before insert on supplier_stats begin select raise(abort, 'boom'); end");
        Exec("create trigger fail_stats_update before update on supplier_stats begin select raise(abort, 'boom'); end");
        var changed = TestBodies.Valid(updatedAt: TestBodies.LaterUpdatedAt);
        changed["roomId"] = "room-9";

        var result = Ingest(changed);

        Assert.That(result, Is.InstanceOf<IngestResult.Failed>());
        using var connection = _db.Factory.Open();
        Assert.That(connection.ExecuteScalar<string>("select roomId from reservations"), Is.EqualTo("room-1"), "the update must be rolled back");
        Assert.That(Count("request_log"), Is.EqualTo(2), "the second request's slot is kept");
        Exec("drop trigger fail_stats_insert");
        Exec("drop trigger fail_stats_update");
        Assert.That(Stats(), Is.EqualTo(Counters(ingested: 1)));
    }

    [Test]
    public void Failure_while_counting_an_invalid_request_also_keeps_the_slot()
    {
        Exec("create trigger fail_stats_insert before insert on supplier_stats begin select raise(abort, 'boom'); end");

        var result = Ingest(TestBodies.Invalid());

        Assert.That(result, Is.InstanceOf<IngestResult.Failed>());
        Assert.That(Count("request_log"), Is.EqualTo(1));
    }
}
