namespace SupplierFeed.Api.Domain;

/// <summary>A fully validated ingest request, normalized for storage (UTC epoch milliseconds, exact decimal price).</summary>
public sealed record ValidatedIngest(
    string SupplierId,
    string ReservationId,
    string RoomId,
    long CheckInMs,
    long CheckOutMs,
    decimal Price,
    long UpdatedAtMs);
