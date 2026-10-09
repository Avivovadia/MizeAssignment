namespace SupplierFeed.Api.Data;

public sealed class CleanupOptions
{
    public const string SectionName = "Cleanup";

    /// <summary>How often each instance removes request_log rows that have left the throttle window.</summary>
    public int IntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Rows deleted per transaction. Each batch is its own short write transaction so a large backlog
    /// (after downtime, say) never holds the single writer lock for long.
    /// </summary>
    public int BatchSize { get; init; } = 1_000;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(IntervalSeconds, 1, nameof(IntervalSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));
    }
}
