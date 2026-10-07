using Dapper;
using Microsoft.Data.Sqlite;
using SupplierFeed.Api.Data;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class DatabaseInitializerTests
{
    private TestDatabase _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private const string InsertReservation = @"
        insert into reservations (supplierId, reservationId, roomId, checkInMs, checkOutMs, price, updatedAtMs)
        values (@supplierId, @reservationId, 'room-1', 1000, 2000, @price, 3000)";

    [Test]
    public void Initialize_creates_tables_and_window_index()
    {
        using var conn = _db.Factory.Open();

        var names = conn.Query<string>("select name from sqlite_master where name not like 'sqlite_%'").ToList();

        Assert.That(names, Is.SupersetOf(new[] { "reservations", "request_log", "supplier_stats", "ix_request_log_supplier_ts" }));
    }

    [Test]
    public void Initialize_is_idempotent_and_keeps_existing_data()
    {
        using (var conn = _db.Factory.Open())
            conn.Execute(InsertReservation, new { supplierId = "s1", reservationId = "r1", price = 450.00m });

        Assert.DoesNotThrow(() => DatabaseInitializer.Initialize(_db.Factory));

        using var check = _db.Factory.Open();
        Assert.That(check.ExecuteScalar<long>("select count(*) from reservations"), Is.EqualTo(1));
    }

 
    [Test]
    public void Reservation_key_rejects_duplicate_for_same_supplier()
    {
        using var conn = _db.Factory.Open();
        conn.Execute(InsertReservation, new { supplierId = "s1", reservationId = "r1", price = 1m });

        Assert.Throws<SqliteException>(() =>
            conn.Execute(InsertReservation, new { supplierId = "s1", reservationId = "r1", price = 2m }));
    }

    [Test]
    public void Same_reservation_id_is_allowed_under_different_suppliers()
    {
        using var conn = _db.Factory.Open();
        conn.Execute(InsertReservation, new { supplierId = "s1", reservationId = "r1", price = 1m });

        Assert.DoesNotThrow(() =>
            conn.Execute(InsertReservation, new { supplierId = "s2", reservationId = "r1", price = 1m }));
    }

    [Test]
    public void Price_is_stored_as_text_and_round_trips_exactly()
    {
        // A double cannot hold this value; a NUMERIC column would silently turn it into one.
        const decimal precise = 450.1000000000000001m;
        using var conn = _db.Factory.Open();
        conn.Execute(InsertReservation, new { supplierId = "s1", reservationId = "r1", price = precise });

        var storedType = conn.ExecuteScalar<string>("select typeof(price) from reservations");
        var stored = conn.ExecuteScalar<decimal>("select price from reservations");

        Assert.That(storedType, Is.EqualTo("text"));
        Assert.That(stored, Is.EqualTo(precise));
    }

    [Test]
    public void Supplier_stats_counters_default_to_zero()
    {
        using var conn = _db.Factory.Open();
        conn.Execute("insert into supplier_stats (supplierId) values ('s1')");

        var row = conn.QuerySingle("select ingested, ignored, invalid, throttled from supplier_stats where supplierId = 's1'");

        Assert.That((long)row.ingested, Is.Zero);
        Assert.That((long)row.ignored, Is.Zero);
        Assert.That((long)row.invalid, Is.Zero);
        Assert.That((long)row.throttled, Is.Zero);
    }
}
