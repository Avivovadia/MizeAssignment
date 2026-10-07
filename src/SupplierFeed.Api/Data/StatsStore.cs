using System.Data;
using Dapper;
using SupplierFeed.Api.Domain;

namespace SupplierFeed.Api.Data;

public sealed record SupplierStats(string SupplierId, long Ingested, long Ignored, long Invalid, long Throttled);

/// <summary>
/// Owns the per-supplier counters. Callers hand it the result of what happened and it alone decides which
/// counter that belongs to, so the throttle and reservation stores never touch stats. Runs on the caller's
/// connection and transaction, so counters commit or roll back together with the request.
/// </summary>
public sealed class StatsStore
{
    private const string IngestedColumn = "ingested";
    private const string IgnoredColumn = "ignored";
    private const string InvalidColumn = "invalid";
    private const string ThrottledColumn = "throttled";

    // Built once from the constants above; the column names are never caller input.
    private static readonly string IncrementIngestedSql = IncrementSql(IngestedColumn);
    private static readonly string IncrementIgnoredSql = IncrementSql(IgnoredColumn);
    private static readonly string IncrementInvalidSql = IncrementSql(InvalidColumn);
    private static readonly string IncrementThrottledSql = IncrementSql(ThrottledColumn);

    private const string SelectSql = @"
        select supplierId, ingested, ignored, invalid, throttled
        from supplier_stats
        where supplierId = @supplierId";

    /// <summary>A reservation was applied: created and updated are ingested, duplicate and out of date are ignored.</summary>
    public void Record(IDbConnection connection, IDbTransaction transaction, string supplierId, IngestDetail detail)
    {
        var sql = detail switch
        {
            IngestDetail.Created or IngestDetail.Updated => IncrementIngestedSql,
            IngestDetail.Duplicate or IngestDetail.OutOfDate => IncrementIgnoredSql,
            _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "No counter is defined for this outcome."),
        };
        connection.Execute(sql, new { supplierId }, transaction);
    }

    /// <summary>A request was admitted but its payload was rejected.</summary>
    public void Record(IDbConnection connection, IDbTransaction transaction, string supplierId, IngestParseResult.Invalid invalid) =>
        connection.Execute(IncrementInvalidSql, new { supplierId }, transaction);

    /// <summary>A request was rejected by the rate limit.</summary>
    public void Record(IDbConnection connection, IDbTransaction transaction, string supplierId, ThrottleDecision.Throttled throttled) =>
        connection.Execute(IncrementThrottledSql, new { supplierId }, transaction);

    /// <summary>The supplier's counters; all zero if it has never been seen.</summary>
    public SupplierStats Get(IDbConnection connection, string supplierId) =>
        connection.QuerySingleOrDefault<SupplierStats>(SelectSql, new { supplierId })
        ?? new SupplierStats(supplierId, 0, 0, 0, 0);

    private static string IncrementSql(string column) => $@"
        insert into supplier_stats (supplierId, {column}) values (@supplierId, 1)
        on conflict (supplierId) do update set {column} = {column} + 1";
}
