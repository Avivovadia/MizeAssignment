namespace SupplierFeed.Api.Domain;

/// <summary>
/// A validation that needs the database clock, so it runs in the orchestrator right after the pure
/// parse validation. One far-future updatedAtUtc would otherwise make every later real update out of date.
/// </summary>
public sealed class UpdatedAtPolicy
{
    private readonly TimeSpan _maxFutureSkew;

    public UpdatedAtPolicy(UpdatedAtOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxFutureSkewSeconds, nameof(options.MaxFutureSkewSeconds));
        _maxFutureSkew = TimeSpan.FromSeconds(options.MaxFutureSkewSeconds);
    }

    /// <summary>Returns an error message when the timestamp is too far ahead of <paramref name="nowMs"/>, otherwise null.</summary>
    public string? Check(long updatedAtMs, long nowMs) =>
        updatedAtMs > nowMs + (long)_maxFutureSkew.TotalMilliseconds
            ? $"{IngestFields.UpdatedAtUtc} must not be more than {_maxFutureSkew.TotalSeconds} seconds ahead of the server time"
            : null;
}
