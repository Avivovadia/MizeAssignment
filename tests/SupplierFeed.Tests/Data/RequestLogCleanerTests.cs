using Dapper;
using SupplierFeed.Api.Data;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class RequestLogCleanerTests
{
    private const long Now = 1_800_000_000_000;
    private const long WindowMs = 60_000;

    private TestDatabase _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private RequestLogCleaner Cleaner(int windowSeconds = 60, int batchSize = 1000) =>
        new(_db.Factory, new ThrottleOptions { WindowSeconds = windowSeconds }, new CleanupOptions { BatchSize = batchSize });

    // ---- what gets deleted ----

    [Test]
    public void Rows_older_than_the_window_are_deleted_and_rows_inside_it_are_kept()
    {
        _db.InsertLogRowsAt("s1", 5, Now - 61_000);
        _db.InsertLogRowsAt("s1", 3, Now - 30_000);

        var deleted = Cleaner().RunAt(Now);

        Assert.That(deleted, Is.EqualTo(5));
        Assert.That(_db.Count("request_log"), Is.EqualTo(3));
    }

    [Test]
    public void A_row_exactly_one_window_old_is_deleted_and_one_millisecond_younger_is_kept()
    {
        // The exact complement of the throttle, which counts rows with tsMs > now - window.
        _db.InsertLogRowsAt("s1", 1, Now - WindowMs);
        _db.InsertLogRowsAt("s1", 1, Now - WindowMs + 1);

        Cleaner().RunAt(Now);

        using var connection = _db.Factory.Open();
        Assert.That(connection.ExecuteScalar<long>("select tsMs from request_log"), Is.EqualTo(Now - WindowMs + 1));
    }

    [Test]
    public void Every_suppliers_expired_rows_are_deleted_in_one_pass()
    {
        foreach (var supplier in new[] { "a", "b", "c" })
            _db.InsertLogRowsAt(supplier, 4, Now - 120_000);

        Assert.That(Cleaner().RunAt(Now), Is.EqualTo(12));
        Assert.That(_db.Count("request_log"), Is.Zero);
    }

    [Test]
    public void Nothing_to_delete_returns_zero()
    {
        _db.InsertLogRowsAt("s1", 3, Now - 1_000);

        Assert.That(Cleaner().RunAt(Now), Is.Zero);
        Assert.That(_db.Count("request_log"), Is.EqualTo(3));
    }

    [Test]
    public void The_window_comes_from_the_throttle_options()
    {
        _db.InsertLogRowsAt("s1", 2, Now - 10_000);

        Cleaner(windowSeconds: 5).RunAt(Now);

        Assert.That(_db.Count("request_log"), Is.Zero);
    }

    [Test]
    public void Batches_smaller_than_the_backlog_still_delete_everything()
    {
        _db.InsertLogRowsAt("s1", 2_500, Now - 120_000);
        _db.InsertLogRowsAt("s1", 7, Now - 1_000);

        var deleted = Cleaner(batchSize: 1_000).RunAt(Now);

        Assert.That(deleted, Is.EqualTo(2_500));
        Assert.That(_db.Count("request_log"), Is.EqualTo(7));
    }

    [Test]
    public void A_backlog_that_is_an_exact_multiple_of_the_batch_size_is_fully_deleted()
    {
        _db.InsertLogRowsAt("s1", 2_000, Now - 120_000);

        Assert.That(Cleaner(batchSize: 1_000).RunAt(Now), Is.EqualTo(2_000));
        Assert.That(_db.Count("request_log"), Is.Zero);
    }

    // ---- the guarantee that matters: cleaning never changes what the throttle would decide ----

    [Test]
    public void The_count_the_throttle_would_see_is_identical_before_and_after_a_pass_at_every_later_instant()
    {
        foreach (var ageSeconds in new[] { 0, 1, 7, 29, 30, 31, 45, 59, 60, 61, 90, 120, 600 })
            _db.InsertLogRowsAt("s1", 3, Now - ageSeconds * 1_000L);

        long WindowCountAt(long instant)
        {
            using var connection = _db.Factory.Open();
            return connection.ExecuteScalar<long>(
                "select count(*) from request_log where supplierId = 's1' and tsMs > @cutoff", new { cutoff = instant - WindowMs });
        }

        var instants = Enumerable.Range(0, 61).Select(s => Now + s * 1_000L).ToArray();
        var before = instants.Select(WindowCountAt).ToArray();

        Cleaner().RunAt(Now);

        Assert.That(instants.Select(WindowCountAt), Is.EqualTo(before));
    }

    // ---- options ----

    [TestCase(0, 1000)]
    [TestCase(-5, 1000)]
    [TestCase(60, 0)]
    [TestCase(60, -1)]
    public void Non_positive_options_are_rejected(int intervalSeconds, int batchSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestLogCleaner(
            _db.Factory, new ThrottleOptions(), new CleanupOptions { IntervalSeconds = intervalSeconds, BatchSize = batchSize }));
    }

    [Test]
    public void Defaults_are_a_one_minute_interval_and_batches_of_a_thousand()
    {
        var options = new CleanupOptions();

        Assert.That(options.IntervalSeconds, Is.EqualTo(60));
        Assert.That(options.BatchSize, Is.EqualTo(1_000));
    }
}
