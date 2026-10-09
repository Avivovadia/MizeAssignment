using Dapper;

namespace SupplierFeed.Api.Data;

/// <summary>
/// Deletes request_log rows that have left the throttle window. Correctness never depends on it: the throttle
/// only counts rows with tsMs > now - window, so expired rows are dead weight that only costs table size.
/// The cutoff is the exact complement of that rule, so a pass can never change what the throttle decides.
/// </summary>
public sealed class RequestLogCleaner
{
    // A scan per batch: the (supplierId, tsMs) index cannot serve a range on tsMs alone, and a second index
    // would slow the hot insert path. The table is small (about the limit per supplier), so a scan is cheap.
    private const string DeleteBatchSql = @"
        delete from request_log
        where rowid in (select rowid from request_log where tsMs <= @cutoffMs limit @batchSize)";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly long _windowMs;
    private readonly int _batchSize;

    public RequestLogCleaner(SqliteConnectionFactory connectionFactory, ThrottleOptions throttle, CleanupOptions cleanup)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(throttle.WindowSeconds, 1, nameof(throttle.WindowSeconds));
        cleanup.Validate();

        _connectionFactory = connectionFactory;
        _windowMs = (long)TimeSpan.FromSeconds(throttle.WindowSeconds).TotalMilliseconds;
        _batchSize = cleanup.BatchSize;
    }

    /// <summary>One full pass using the database clock. Returns how many rows were deleted.</summary>
    public long Run() => RunPass(fixedNowMs: null);

    /// <summary>One full pass at a given instant. Internal so tests can fix the clock.</summary>
    internal long RunAt(long nowMs) => RunPass(nowMs);

    private long RunPass(long? fixedNowMs)
    {
        long total = 0;
        while (true)
        {
            // Each batch is its own short write transaction, so other requests get the lock in between.
            using var connection = _connectionFactory.Open();
            using var transaction = connection.BeginWriteTransaction();

            var nowMs = fixedNowMs ?? DbClock.NowMs(connection, transaction);
            var deleted = connection.Execute(DeleteBatchSql, new { cutoffMs = nowMs - _windowMs, batchSize = _batchSize }, transaction);
            transaction.Commit();

            total += deleted;
            if (deleted < _batchSize)
                return total;
        }
    }
}
