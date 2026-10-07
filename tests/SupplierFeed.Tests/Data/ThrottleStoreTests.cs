using Dapper;
using Microsoft.Data.Sqlite;
using SupplierFeed.Api.Data;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class ThrottleStoreTests
{
    // Backdated rows use margins far from the 60s boundary so timing never makes a test flaky.
    private const long RecentAgeMs = 10_000;
    private const long HalfWindowAgeMs = 30_000;
    private const long ExpiredAgeMs = 61_000;

    private TestDatabase _db = null!;
    private ThrottleStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
        _store = new ThrottleStore(new ThrottleOptions());
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private ThrottleDecision Admit(string supplierId, ThrottleStore? store = null)
    {
        using var connection = _db.Factory.Open();
        using var transaction = connection.BeginTransaction();
        var decision = (store ?? _store).TryAdmit(connection, transaction, supplierId);
        transaction.Commit();
        return decision;
    }

    [Test]
    public void First_hundred_requests_are_admitted_and_the_101st_is_throttled()
    {
        for (var i = 1; i <= 100; i++)
            Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Admitted>(), $"request {i}");

        Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Throttled>());
    }

    [Test]
    public void Throttled_requests_add_no_row_so_they_never_extend_the_block()
    {
        _db.InsertLogRows("s1", 100, RecentAgeMs);

        for (var i = 0; i < 5; i++)
            Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Throttled>());

        Assert.That(_db.LogCount("s1"), Is.EqualTo(100));
    }

    [Test]
    public void Rows_older_than_the_window_do_not_count()
    {
        _db.InsertLogRows("s1", 100, ExpiredAgeMs);

        Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Admitted>());
    }

    [Test]
    public void One_expired_row_frees_exactly_one_slot()
    {
        _db.InsertLogRows("s1", 99, RecentAgeMs);
        _db.InsertLogRows("s1", 1, ExpiredAgeMs);

        Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Admitted>());
        Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Throttled>());
    }

    // The store reads the clock inside the caller's transaction and relies on that transaction already
    // holding the write lock (otherwise another writer could slip in between the clock read and the
    // insert, leaving the admit row stamped too early). This pins the library default it depends on.
    [Test]
    public void Default_transactions_take_the_write_lock_before_any_statement_runs()
    {
        using var holder = _db.Factory.Open();
        using var transaction = holder.BeginTransaction(); // no statement executed yet

        using var other = new SqliteConnectionFactory($"{_db.ConnectionString};Default Timeout=1").Open();
        var exception = Assert.Throws<SqliteException>(() =>
            other.Execute("insert into request_log (supplierId, tsMs) values ('other', 1)"));

        Assert.That(exception!.SqliteErrorCode, Is.EqualTo(5)); // SQLITE_BUSY
    }

    // ---- fixed-clock tests: the store is given the instant, rows sit at exact timestamps ----

    private const long Now = 1_800_000_000_000;
    private const long WindowMs = 60_000;

    private ThrottleDecision AdmitAt(long nowMs, string supplierId = "s1")
    {
        using var connection = _db.Factory.Open();
        using var transaction = connection.BeginTransaction();
        var decision = _store.TryAdmitAt(connection, transaction, supplierId, nowMs);
        transaction.Commit();
        return decision;
    }

    private static long RetryAfterMs(ThrottleDecision decision)
    {
        Assert.That(decision, Is.InstanceOf<ThrottleDecision.Throttled>());
        return ((ThrottleDecision.Throttled)decision).RetryAfterMs;
    }

    [Test]
    public void Retry_after_is_the_exact_milliseconds_until_the_oldest_row_leaves_the_window()
    {
        _db.InsertLogRowsAt("s1", 100, Now - 30_000);

        Assert.That(RetryAfterMs(AdmitAt(Now)), Is.EqualTo(30_000));
    }

    [Test]
    public void Retry_after_follows_the_oldest_row_not_the_newest()
    {
        _db.InsertLogRowsAt("s1", 1, Now - 50_000);
        _db.InsertLogRowsAt("s1", 99, Now - 10_000);

        Assert.That(RetryAfterMs(AdmitAt(Now)), Is.EqualTo(10_000));
    }

    [Test]
    public void A_row_one_millisecond_from_expiring_still_throttles_and_reports_one_millisecond_never_zero()
    {
        _db.InsertLogRowsAt("s1", 100, Now - (WindowMs - 1));

        Assert.That(RetryAfterMs(AdmitAt(Now)), Is.EqualTo(1));
    }

    [Test]
    public void A_row_exactly_one_window_old_is_outside_the_window()
    {
        _db.InsertLogRowsAt("s1", 100, Now - WindowMs);

        Assert.That(AdmitAt(Now), Is.InstanceOf<ThrottleDecision.Admitted>());
    }

    [Test]
    public void Retrying_after_the_reported_time_is_admitted()
    {
        _db.InsertLogRowsAt("s1", 100, Now - 30_000);
        var retryAfterMs = RetryAfterMs(AdmitAt(Now));

        Assert.That(AdmitAt(Now + retryAfterMs), Is.InstanceOf<ThrottleDecision.Admitted>());
    }

    [Test]
    public void Retrying_one_millisecond_too_early_is_still_throttled()
    {
        _db.InsertLogRowsAt("s1", 100, Now - 30_000);
        var retryAfterMs = RetryAfterMs(AdmitAt(Now));

        Assert.That(AdmitAt(Now + retryAfterMs - 1), Is.InstanceOf<ThrottleDecision.Throttled>());
    }

    [Test]
    public void Throttled_decision_reports_the_configured_limit_and_window()
    {
        var store = new ThrottleStore(new ThrottleOptions { Limit = 3, WindowSeconds = 20 });
        _db.InsertLogRows("s1", 3, 5_000);

        var decision = Admit("s1", store);

        Assert.That(decision, Is.InstanceOf<ThrottleDecision.Throttled>());
        var throttled = (ThrottleDecision.Throttled)decision;
        Assert.That(throttled.Limit, Is.EqualTo(3));
        Assert.That(throttled.WindowSeconds, Is.EqualTo(20));
    }

    [Test]
    public void Suppliers_are_throttled_independently()
    {
        _db.InsertLogRows("s1", 100, RecentAgeMs);

        Assert.That(Admit("s1"), Is.InstanceOf<ThrottleDecision.Throttled>());
        Assert.That(Admit("s2"), Is.InstanceOf<ThrottleDecision.Admitted>());
    }

    [Test]
    public void Limit_and_window_come_from_options()
    {
        var store = new ThrottleStore(new ThrottleOptions { Limit = 3 });

        for (var i = 0; i < 3; i++)
            Assert.That(Admit("s1", store), Is.InstanceOf<ThrottleDecision.Admitted>());

        Assert.That(Admit("s1", store), Is.InstanceOf<ThrottleDecision.Throttled>());
    }

    [Test]
    public void Window_length_comes_from_options()
    {
        var store = new ThrottleStore(new ThrottleOptions { Limit = 100, WindowSeconds = 5 });
        _db.InsertLogRows("s1", 100, RecentAgeMs); // 10s old: outside a 5s window

        Assert.That(Admit("s1", store), Is.InstanceOf<ThrottleDecision.Admitted>());
    }

    [Test]
    public void Rolling_back_the_transaction_removes_the_admit_row()
    {
        using (var connection = _db.Factory.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Assert.That(_store.TryAdmit(connection, transaction, "s1"), Is.InstanceOf<ThrottleDecision.Admitted>());
            transaction.Rollback();
        }

        Assert.That(_db.LogCount("s1"), Is.Zero);
    }

    [TestCase(0, 60)]
    [TestCase(-1, 60)]
    [TestCase(100, 0)]
    [TestCase(100, -5)]
    public void Non_positive_options_are_rejected(int limit, int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ThrottleStore(new ThrottleOptions { Limit = limit, WindowSeconds = windowSeconds }));
    }

    [Test]
    public async Task Parallel_requests_on_separate_connections_admit_exactly_the_limit()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 150)
            .Select(_ => Task.Run(() => Admit("s1"))));

        Assert.That(results.OfType<ThrottleDecision.Admitted>().Count(), Is.EqualTo(100));
        Assert.That(results.OfType<ThrottleDecision.Throttled>().Count(), Is.EqualTo(50));
        Assert.That(_db.LogCount("s1"), Is.EqualTo(100));
    }
}
