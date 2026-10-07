using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupplierFeed.Api.Domain;

/// <summary>Same vocabulary as the stats counters; 'invalid' (400) is not a counter.</summary>
public enum IngestStatus
{
    Ingested,
    Ignored,
    Throttled,
}

public enum IngestDetail
{
    Created,
    Updated,
    Duplicate,
    OutOfDate,
}

public sealed record IngestResponse(IngestStatus Status, IngestDetail? Detail = null, int? RetryAfterSeconds = null);

public sealed record ErrorResponse(IReadOnlyList<string> Errors);

/// <summary>JSON settings for API responses: camelCase properties, lowercase enum words ("outofdate").</summary>
public static class IngestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // API-only JSON (never embedded in HTML), so keep messages like "price must be >= 0" readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(new LowerCaseNamingPolicy(), allowIntegerValues: false) },
    };

    private sealed class LowerCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }
}
