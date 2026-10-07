using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupplierFeed.Api.Domain;

/// <summary>Same vocabulary as the stats counters.</summary>
public enum IngestStatus
{
    Ingested,
    Ignored,
    Invalid,
    Throttled,
}

public enum IngestDetail
{
    Created,
    Updated,
    Duplicate,
    OutOfDate,
}

/// <param name="RetryAfterMs">Exact time until the supplier can retry. The HTTP Retry-After header can only carry whole seconds, so it is rounded up there.</param>
/// <param name="Limit">Throttled only: the configured limit that was exceeded.</param>
/// <param name="WindowSeconds">Throttled only: the window the limit applies to (e.g. 100 requests per 60 seconds).</param>
public sealed record IngestResponse(
    IngestStatus Status,
    IngestDetail? Detail = null,
    long? RetryAfterMs = null,
    int? Limit = null,
    int? WindowSeconds = null);

public sealed record ErrorResponse(IReadOnlyList<string> Errors)
{
    [JsonPropertyOrder(-1)]
    public IngestStatus Status => IngestStatus.Invalid;
}

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
