using System.Globalization;
using System.Text.Json;

namespace SupplierFeed.Api.Domain;

/// <summary>
/// Reads an ingest body in two stages: first the supplierId, tolerantly (it decides whether the
/// request can be throttled at all), then the remaining fields strictly.
/// </summary>
public static class IngestParser
{
    private static class Field
    {
        public const string SupplierId = "supplierId";
        public const string ReservationId = "reservationId";
        public const string RoomId = "roomId";
        public const string CheckIn = "checkIn";
        public const string CheckOut = "checkOut";
        public const string Price = "price";
        public const string UpdatedAtUtc = "updatedAtUtc";
    }

    private static class Message
    {
        public const string CheckOutNotAfterCheckIn = $"{Field.CheckOut} must be after {Field.CheckIn}";
        public const string NegativePrice = $"{Field.Price} must be >= 0";

        public static string Required(string field) => $"{field} is required";
        public static string MustBeString(string field) => $"{field} must be a string";
        public static string MustBeNumber(string field) => $"{field} must be a number";
        public static string MustBeTimestamp(string field) => $"{field} must be an ISO 8601 timestamp, e.g. {TimestampExample}";
    }

    private const string TimestampExample = "2026-08-01T00:00:00Z";

    private static readonly string[] TimestampFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
    };

    public static IngestParseResult Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new IngestParseResult.Unattributable();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new IngestParseResult.Unattributable();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, Field.SupplierId, out var supplierElement)
                || supplierElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(supplierElement.GetString()))
            {
                return new IngestParseResult.Unattributable();
            }

            var supplierId = supplierElement.GetString()!;
            var errors = new List<string>();

            var reservationId = ReadText(root, Field.ReservationId, errors);
            var roomId = ReadText(root, Field.RoomId, errors);
            var checkIn = ReadTimestamp(root, Field.CheckIn, errors);
            var checkOut = ReadTimestamp(root, Field.CheckOut, errors);
            var price = ReadPrice(root, errors);
            var updatedAt = ReadTimestamp(root, Field.UpdatedAtUtc, errors);

            if (checkIn is { } from && checkOut is { } to && to <= from)
                errors.Add(Message.CheckOutNotAfterCheckIn);

            if (errors.Count > 0)
                return new IngestParseResult.Invalid(supplierId, errors);

            return new IngestParseResult.Valid(new ValidatedIngest(
                supplierId,
                reservationId!,
                roomId!,
                checkIn!.Value.ToUnixTimeMilliseconds(),
                checkOut!.Value.ToUnixTimeMilliseconds(),
                price!.Value,
                updatedAt!.Value.ToUnixTimeMilliseconds()));
        }
    }

    private static string? ReadText(JsonElement root, string field, List<string> errors)
    {
        if (!TryGetProperty(root, field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors.Add(Message.Required(field));
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(Message.MustBeString(field));
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(Message.Required(field));
            return null;
        }

        return value;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string field, List<string> errors)
    {
        if (!TryGetProperty(root, field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors.Add(Message.Required(field));
            return null;
        }

        // AssumeUniversal: a timestamp with no offset is UTC, not the server's local time.
        if (element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(element.GetString(), TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        errors.Add(Message.MustBeTimestamp(field));
        return null;
    }

    private static decimal? ReadPrice(JsonElement root, List<string> errors)
    {
        if (!TryGetProperty(root, Field.Price, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors.Add(Message.Required(Field.Price));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var price))
        {
            errors.Add(Message.MustBeNumber(Field.Price));
            return null;
        }

        if (price < 0)
        {
            errors.Add(Message.NegativePrice);
            return null;
        }

        return price;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
