using System.Text.Json.Nodes;

namespace SupplierFeed.Tests.Support;

internal static class TestBodies
{
    public const string DefaultUpdatedAt = "2026-07-01T10:15:00Z";
    public const string LaterUpdatedAt = "2026-07-02T10:15:00Z";
    public const string EarlierUpdatedAt = "2026-06-30T10:15:00Z";

    public static JsonObject Valid(string supplierId = "s1", string reservationId = "r1", string updatedAt = DefaultUpdatedAt) => new()
    {
        ["supplierId"] = supplierId,
        ["reservationId"] = reservationId,
        ["roomId"] = "room-1",
        ["checkIn"] = "2026-08-01T00:00:00Z",
        ["checkOut"] = "2026-08-03T00:00:00Z",
        ["price"] = 450.00m,
        ["updatedAtUtc"] = updatedAt,
    };

    /// <summary>Attributable (has a supplierId) but rejected by validation.</summary>
    public static JsonObject Invalid(string supplierId = "s1")
    {
        var body = Valid(supplierId);
        body["price"] = -1m;
        return body;
    }
}
