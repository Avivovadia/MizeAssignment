using Dapper;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class ReservationStoreTests
{
    private const long T1 = 1_000, T2 = 2_000, T3 = 3_000;

    private TestDatabase _db = null!;
    private ReservationStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
        _store = new ReservationStore();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static ValidatedIngest Request(long updatedAtMs = T2) => new(
        SupplierId: "s1",
        ReservationId: "r1",
        RoomId: "room-1",
        CheckInMs: 10_000,
        CheckOutMs: 20_000,
        Price: 450.00m,
        UpdatedAtMs: updatedAtMs);

    /// <summary>Applies one request and reports the outcome and how many rows SQLite actually changed.</summary>
    private (IngestDetail Outcome, long RowsChanged) Apply(ValidatedIngest request)
    {
        using var connection = _db.Factory.Open();
        var before = connection.ExecuteScalar<long>("select total_changes()");
        using var transaction = connection.BeginTransaction();
        var outcome = _store.Apply(connection, transaction, request);
        transaction.Commit();
        return (outcome, connection.ExecuteScalar<long>("select total_changes()") - before);
    }

    // The provider stores decimals as exact text but drops trailing zeros ("450.00" is saved as "450.0"),
    // so the price is read back as a number: equal values are what matters, not their formatting.
    private (string RoomId, long CheckInMs, long CheckOutMs, decimal Price, long UpdatedAtMs) Stored(
        string supplierId = "s1", string reservationId = "r1")
    {
        using var connection = _db.Factory.Open();
        var row = connection.QuerySingle<(string RoomId, long CheckInMs, long CheckOutMs, string Price, long UpdatedAtMs)>(
            @"select roomId, checkInMs, checkOutMs, price, updatedAtMs from reservations
              where supplierId = @supplierId and reservationId = @reservationId",
            new { supplierId, reservationId });
        return (row.RoomId, row.CheckInMs, row.CheckOutMs, decimal.Parse(row.Price, System.Globalization.CultureInfo.InvariantCulture), row.UpdatedAtMs);
    }

    private long RowCount()
    {
        using var connection = _db.Factory.Open();
        return connection.ExecuteScalar<long>("select count(*) from reservations");
    }

    // ---- created ----

    [Test]
    public void New_reservation_is_created_with_all_fields()
    {
        var (outcome, rowsChanged) = Apply(Request(T2));

        Assert.That(outcome, Is.EqualTo(IngestDetail.Created));
        Assert.That(rowsChanged, Is.EqualTo(1));
        Assert.That(Stored(), Is.EqualTo(("room-1", 10_000L, 20_000L, 450.00m, T2)));
    }

    [Test]
    public void Same_reservation_id_under_another_supplier_is_a_separate_reservation()
    {
        Apply(Request());

        var (outcome, _) = Apply(Request() with { SupplierId = "s2" });

        Assert.That(outcome, Is.EqualTo(IngestDetail.Created));
        Assert.That(RowCount(), Is.EqualTo(2));
    }

    // ---- duplicate: identical details, never rewrites the business fields ----

    [Test]
    public void Identical_request_is_a_duplicate_and_writes_nothing()
    {
        Apply(Request(T2));

        var (outcome, rowsChanged) = Apply(Request(T2));

        Assert.That(outcome, Is.EqualTo(IngestDetail.Duplicate));
        Assert.That(rowsChanged, Is.Zero);
    }

    [Test]
    public void Duplicate_with_a_newer_version_only_bumps_the_stored_version()
    {
        Apply(Request(T2));

        var (outcome, rowsChanged) = Apply(Request(T3));

        Assert.That(outcome, Is.EqualTo(IngestDetail.Duplicate));
        Assert.That(rowsChanged, Is.EqualTo(1));
        Assert.That(Stored(), Is.EqualTo(("room-1", 10_000L, 20_000L, 450.00m, T3)));
    }

    [Test]
    public void Duplicate_with_an_older_version_writes_nothing_and_keeps_the_newer_version()
    {
        Apply(Request(T3));

        var (outcome, rowsChanged) = Apply(Request(T1));

        Assert.That(outcome, Is.EqualTo(IngestDetail.Duplicate));
        Assert.That(rowsChanged, Is.Zero);
        Assert.That(Stored().UpdatedAtMs, Is.EqualTo(T3));
    }

    [Test]
    public void Price_is_compared_as_a_number_not_as_text()
    {
        Apply(Request() with { Price = 450.00m });

        var (outcome, rowsChanged) = Apply(Request() with { Price = 450.0m });

        Assert.That(outcome, Is.EqualTo(IngestDetail.Duplicate));
        Assert.That(rowsChanged, Is.Zero);
    }

    // ---- updated / out of date: details differ ----

    [Test]
    public void Changed_details_with_a_newer_version_update_every_field()
    {
        Apply(Request(T2));
        var changed = Request(T3) with { RoomId = "room-9", CheckInMs = 11_000, CheckOutMs = 22_000, Price = 500.50m };

        var (outcome, rowsChanged) = Apply(changed);

        Assert.That(outcome, Is.EqualTo(IngestDetail.Updated));
        Assert.That(rowsChanged, Is.EqualTo(1));
        Assert.That(Stored(), Is.EqualTo(("room-9", 11_000L, 22_000L, 500.50m, T3)));
    }

    [Test]
    public void Changed_details_with_an_equal_version_are_an_update_not_out_of_date()
    {
        Apply(Request(T2));

        var (outcome, _) = Apply(Request(T2) with { RoomId = "room-9" });

        Assert.That(outcome, Is.EqualTo(IngestDetail.Updated));
        Assert.That(Stored().RoomId, Is.EqualTo("room-9"));
    }

    [Test]
    public void Changed_details_with_an_older_version_are_out_of_date_and_keep_the_stored_data()
    {
        Apply(Request(T3));

        var (outcome, rowsChanged) = Apply(Request(T1) with { RoomId = "room-9", Price = 1m });

        Assert.That(outcome, Is.EqualTo(IngestDetail.OutOfDate));
        Assert.That(rowsChanged, Is.Zero);
        Assert.That(Stored(), Is.EqualTo(("room-1", 10_000L, 20_000L, 450.00m, T3)));
    }

    [TestCaseSource(nameof(SingleFieldChanges))]
    public void Each_detail_field_on_its_own_counts_as_a_change(string field, Func<ValidatedIngest, ValidatedIngest> change)
    {
        Apply(Request(T2));

        var (outcome, _) = Apply(change(Request(T3)));

        Assert.That(outcome, Is.EqualTo(IngestDetail.Updated), field);
    }

    private static IEnumerable<TestCaseData> SingleFieldChanges()
    {
        yield return new TestCaseData("roomId", (Func<ValidatedIngest, ValidatedIngest>)(r => r with { RoomId = "other" }));
        yield return new TestCaseData("checkIn", (Func<ValidatedIngest, ValidatedIngest>)(r => r with { CheckInMs = r.CheckInMs + 1 }));
        yield return new TestCaseData("checkOut", (Func<ValidatedIngest, ValidatedIngest>)(r => r with { CheckOutMs = r.CheckOutMs + 1 }));
        yield return new TestCaseData("price", (Func<ValidatedIngest, ValidatedIngest>)(r => r with { Price = r.Price + 0.01m }));
    }

    // ---- transaction behavior ----

    [Test]
    public void Rolling_back_the_transaction_leaves_no_reservation()
    {
        using (var connection = _db.Factory.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Assert.That(_store.Apply(connection, transaction, Request()), Is.EqualTo(IngestDetail.Created));
            transaction.Rollback();
        }

        Assert.That(RowCount(), Is.Zero);
    }

    [Test]
    public async Task Concurrent_first_requests_for_one_reservation_create_it_exactly_once()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => Apply(Request()).Outcome)));

        Assert.That(outcomes.Count(o => o == IngestDetail.Created), Is.EqualTo(1));
        Assert.That(outcomes.Count(o => o == IngestDetail.Duplicate), Is.EqualTo(19));
        Assert.That(RowCount(), Is.EqualTo(1));
    }
}
