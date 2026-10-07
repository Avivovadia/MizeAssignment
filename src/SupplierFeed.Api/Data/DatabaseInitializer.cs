using Dapper;

namespace SupplierFeed.Api.Data;

public static class DatabaseInitializer
{
    // Timestamps are integer milliseconds since the Unix epoch (UTC).
    // price must stay TEXT: Microsoft.Data.Sqlite stores decimal as text, and a NUMERIC
    // column would silently convert it to a float and lose precision.
    private const string Schema = @"
        create table if not exists reservations (
            supplierId    text    not null,
            reservationId text    not null,
            roomId        text    not null,
            checkInMs     integer not null,
            checkOutMs    integer not null,
            price         text    not null,
            updatedAtMs   integer not null,
            primary key (supplierId, reservationId)
        );

        create table if not exists request_log (
            supplierId text    not null,
            tsMs       integer not null
        );
        create index if not exists ix_request_log_supplier_ts on request_log (supplierId, tsMs);

        create table if not exists supplier_stats (
            supplierId text    primary key,
            ingested   integer not null default 0,
            ignored    integer not null default 0,
            invalid    integer not null default 0,
            throttled  integer not null default 0
        );";

    public static void Initialize(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        connection.Execute(Schema);
        // WAL is stored in the database file, so it only sticks once the file has been written.
        connection.Execute("pragma journal_mode = wal");
    }
}
