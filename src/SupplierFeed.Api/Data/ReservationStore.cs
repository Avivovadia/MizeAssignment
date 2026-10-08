using System.Data;
using System.Globalization;
using Dapper;
using SupplierFeed.Api.Domain;

namespace SupplierFeed.Api.Data;

/// <summary>
/// Applies a valid, already-admitted request to the reservations table and reports what happened.
/// Knows nothing about throttling or stats. It reads the stored row and then writes, so it relies on the
/// caller's transaction already holding the write lock (BeginWriteTransaction);
/// the primary key (supplierId, reservationId) is the backstop against a double insert.
/// </summary>
public sealed class ReservationStore
{
    private const string SelectSql = @"
        select roomId, checkInMs, checkOutMs, price, updatedAtMs
        from reservations
        where supplierId = @supplierId and reservationId = @reservationId";

    private const string InsertSql = @"
        insert into reservations (supplierId, reservationId, roomId, checkInMs, checkOutMs, price, updatedAtMs)
        values (@supplierId, @reservationId, @roomId, @checkInMs, @checkOutMs, @price, @updatedAtMs)";

    private const string UpdateAllSql = @"
        update reservations
        set roomId = @roomId, checkInMs = @checkInMs, checkOutMs = @checkOutMs, price = @price, updatedAtMs = @updatedAtMs
        where supplierId = @supplierId and reservationId = @reservationId";

    private const string BumpVersionSql = @"
        update reservations set updatedAtMs = @updatedAtMs
        where supplierId = @supplierId and reservationId = @reservationId";

    public IngestDetail Apply(IDbConnection connection, IDbTransaction transaction, ValidatedIngest request)
    {
        var parameters = new
        {
            supplierId = request.SupplierId,
            reservationId = request.ReservationId,
            roomId = request.RoomId,
            checkInMs = request.CheckInMs,
            checkOutMs = request.CheckOutMs,
            price = request.Price,
            updatedAtMs = request.UpdatedAtMs,
        };

        var stored = connection.QuerySingleOrDefault<StoredReservation>(SelectSql, parameters, transaction);
        if (stored is null)
        {
            connection.Execute(InsertSql, parameters, transaction);
            return IngestDetail.Created;
        }

        if (stored.HasSameDetailsAs(request))
        {
            // Nothing to apply. A newer version is remembered so a delayed, older retry is still recognized
            // as out of date later; this is the only write a duplicate ever causes.
            if (request.UpdatedAtMs > stored.UpdatedAtMs)
                connection.Execute(BumpVersionSql, parameters, transaction);

            return IngestDetail.Duplicate;
        }

        if (request.UpdatedAtMs < stored.UpdatedAtMs)
            return IngestDetail.OutOfDate;

        // Equal versions with different details count as an update.
        connection.Execute(UpdateAllSql, parameters, transaction);
        return IngestDetail.Updated;
    }

    // Price is stored as exact decimal text and compared as a number, so 450.0 equals 450.00.
    private sealed record StoredReservation(string RoomId, long CheckInMs, long CheckOutMs, string Price, long UpdatedAtMs)
    {
        public bool HasSameDetailsAs(ValidatedIngest request) =>
            RoomId == request.RoomId
            && CheckInMs == request.CheckInMs
            && CheckOutMs == request.CheckOutMs
            && decimal.Parse(Price, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture) == request.Price;
    }
}
