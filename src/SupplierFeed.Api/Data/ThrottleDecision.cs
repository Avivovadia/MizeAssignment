namespace SupplierFeed.Api.Data;

public abstract record ThrottleDecision
{
    private ThrottleDecision() { }

    public sealed record Admitted : ThrottleDecision;

    /// <param name="RetryAfterMs">Exact milliseconds until the oldest in-window request expires (0 if it already has).</param>
    /// <param name="Limit">The configured limit that was exceeded, so the supplier is told why it was throttled.</param>
    /// <param name="WindowSeconds">The configured window the limit applies to.</param>
    public sealed record Throttled(long RetryAfterMs, int Limit, int WindowSeconds) : ThrottleDecision;
}
