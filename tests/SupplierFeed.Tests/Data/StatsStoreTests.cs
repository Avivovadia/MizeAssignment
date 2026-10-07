using System.Data;
using Dapper;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class StatsStoreTests
{
    private TestDatabase _db = null!;
    private StatsStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
        _store = new StatsStore();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private void InTransaction(Action<IDbConnection, IDbTransaction> action)
    {
        using var connection = _db.Factory.Open();
        using var transaction = connection.BeginTransaction();
        action(connection, transaction);
        transaction.Commit();
    }

    private SupplierStats Get(string supplierId)
    {
        using var connection = _db.Factory.Open();
        return _store.Get(connection, supplierId);
    }

    private static ThrottleDecision.Throttled Throttled() => new(RetryAfterMs: 1_000, Limit: 100, WindowSeconds: 60);

    private static IngestParseResult.Invalid Invalid() => new("s1", new[] { "price must be >= 0" });

    // Each result type is passed in as-is; the store alone decides which counter it belongs to.
    public static IEnumerable<TestCaseData> ResultsAndTheirCounter()
    {
        yield return Case("created", (s, c, t) => s.Record(c, t, "s1", IngestDetail.Created), new SupplierStats("s1", 1, 0, 0, 0));
        yield return Case("updated", (s, c, t) => s.Record(c, t, "s1", IngestDetail.Updated), new SupplierStats("s1", 1, 0, 0, 0));
        yield return Case("duplicate", (s, c, t) => s.Record(c, t, "s1", IngestDetail.Duplicate), new SupplierStats("s1", 0, 1, 0, 0));
        yield return Case("outofdate", (s, c, t) => s.Record(c, t, "s1", IngestDetail.OutOfDate), new SupplierStats("s1", 0, 1, 0, 0));
        yield return Case("invalid", (s, c, t) => s.Record(c, t, "s1", Invalid()), new SupplierStats("s1", 0, 0, 1, 0));
        yield return Case("throttled", (s, c, t) => s.Record(c, t, "s1", Throttled()), new SupplierStats("s1", 0, 0, 0, 1));
    }

    private static TestCaseData Case(string name, Action<StatsStore, IDbConnection, IDbTransaction> record, SupplierStats expected) =>
        new TestCaseData(record, expected).SetName($"Recording_{name}_bumps_only_its_own_counter");

    [TestCaseSource(nameof(ResultsAndTheirCounter))]
    public void Each_result_bumps_exactly_its_own_counter(Action<StatsStore, IDbConnection, IDbTransaction> record, SupplierStats expected)
    {
        InTransaction((c, t) => record(_store, c, t));

        Assert.That(Get("s1"), Is.EqualTo(expected));
    }

    [Test]
    public void Counters_accumulate_and_stay_separate_per_supplier()
    {
        InTransaction((c, t) =>
        {
            _store.Record(c, t, "s1", IngestDetail.Created);
            _store.Record(c, t, "s1", IngestDetail.Updated);
            _store.Record(c, t, "s1", Throttled());
            _store.Record(c, t, "s2", IngestDetail.Duplicate);
        });

        Assert.That(Get("s1"), Is.EqualTo(new SupplierStats("s1", 2, 0, 0, 1)));
        Assert.That(Get("s2"), Is.EqualTo(new SupplierStats("s2", 0, 1, 0, 0)));
    }

    [Test]
    public void First_record_creates_the_supplier_row_and_later_ones_reuse_it()
    {
        InTransaction((c, t) =>
        {
            _store.Record(c, t, "s1", IngestDetail.Created);
            _store.Record(c, t, "s1", IngestDetail.Created);
        });

        using var connection = _db.Factory.Open();
        Assert.That(connection.ExecuteScalar<long>("select count(*) from supplier_stats"), Is.EqualTo(1));
    }

    [Test]
    public void Unknown_supplier_has_all_zero_counters()
    {
        Assert.That(Get("nobody"), Is.EqualTo(new SupplierStats("nobody", 0, 0, 0, 0)));
    }

    [Test]
    public void Rolling_back_the_transaction_leaves_no_counts()
    {
        using (var connection = _db.Factory.Open())
        using (var transaction = connection.BeginTransaction())
        {
            _store.Record(connection, transaction, "s1", IngestDetail.Created);
            transaction.Rollback();
        }

        Assert.That(Get("s1"), Is.EqualTo(new SupplierStats("s1", 0, 0, 0, 0)));
    }

    [Test]
    public void An_unknown_detail_value_is_rejected_instead_of_silently_uncounted()
    {
        using var connection = _db.Factory.Open();
        using var transaction = connection.BeginTransaction();

        Assert.Throws<ArgumentOutOfRangeException>(() => _store.Record(connection, transaction, "s1", (IngestDetail)99));
    }
}
