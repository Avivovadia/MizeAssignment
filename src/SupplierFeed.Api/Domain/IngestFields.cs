namespace SupplierFeed.Api.Domain;

/// <summary>The JSON property names of an ingest request: the wire contract, named once.</summary>
internal static class IngestFields
{
    public const string SupplierId = "supplierId";
    public const string ReservationId = "reservationId";
    public const string RoomId = "roomId";
    public const string CheckIn = "checkIn";
    public const string CheckOut = "checkOut";
    public const string Price = "price";
    public const string UpdatedAtUtc = "updatedAtUtc";
}
