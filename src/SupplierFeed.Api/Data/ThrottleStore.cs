using System.Data;
using Dapper;

namespace SupplierFeed.Api.Data;

/// <summary>
/// Sliding-log rate limit kept in request_log. Only decides admit or reject: it knows nothing about
/// validity, stats or reservations. It runs on the caller's connection and transaction, so the admit row
/// commits or rolls back together with whatever else the request does.
/// </summary>
public sealed class ThrottleStore
{
    private const long MillisecondsPerSecond = 1000;

    // The DB's clock. Read once per decision and reused for the count, the insert and the retry time,
    // so the answer is consistent with the decision. The app never invents "now".
    private const string DbNowMsSql = "select cast(unixepoch('subsec') * 1000 as integer)";

    // Count and insert are one statement, so two requests can never both take the last slot.
    private const string AdmitSql = @"
        insert into request_log (supplierId, tsMs)
        select @supplierId, @nowMs
        where (select count(*) from request_log
               where supplierId = @supplierId and tsMs > @nowMs - @windowMs) < @limit";

    // Oldest request still inside the window at the decision instant.
    private const string OldestInWindowSql = @"
        select min(tsMs) from request_log
        where supplierId = @supplierId and tsMs > @nowMs - @windowMs";

    private readonly int _limit;
    private readonly int _windowSeconds;
    private readonly long _windowMs;

    public ThrottleStore(ThrottleOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Limit, 1, nameof(options.Limit));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WindowSeconds, 1, nameof(options.WindowSeconds));

        _limit = options.Limit;
        _windowSeconds = options.WindowSeconds;
        _windowMs = options.WindowSeconds * MillisecondsPerSecond;
    }

    /// <remarks>
    /// Precondition: <paramref name="transaction"/> already holds the write lock (the default
    /// <c>BeginTransaction()</c> of Microsoft.Data.Sqlite does; <c>BeginTransaction(deferred: true)</c> does not).
    /// The clock is read after the lock is taken, so no other writer can change the log between reading
    /// the instant and using it. With a deferred transaction the instant could be stale by the lock wait.
    /// </remarks>
    public ThrottleDecision TryAdmit(IDbConnection connection, IDbTransaction transaction, string supplierId) =>
        TryAdmitAt(connection, transaction, supplierId, connection.ExecuteScalar<long>(DbNowMsSql, transaction: transaction));

    /// <summary>Same as <see cref="TryAdmit"/> at a given instant. Internal so tests can fix the clock.</summary>
    internal ThrottleDecision TryAdmitAt(IDbConnection connection, IDbTransaction transaction, string supplierId, long nowMs)
    {
        var parameters = new { supplierId, nowMs, windowMs = _windowMs, limit = _limit };

        if (connection.Execute(AdmitSql, parameters, transaction) == 1)
            return new ThrottleDecision.Admitted();

        // Exact, not rounded. We only get here when the window held at least the limit at nowMs, and
        // the caller's transaction holds the write lock, so an oldest row exists and it expires after nowMs:
        // the retry time is always positive.
        var oldestMs = connection.ExecuteScalar<long?>(OldestInWindowSql, parameters, transaction)
            ?? throw new InvalidOperationException("Throttled with an empty window: the transaction must be a write transaction.");
        return new ThrottleDecision.Throttled(oldestMs + _windowMs - nowMs, _limit, _windowSeconds);
    }
}
