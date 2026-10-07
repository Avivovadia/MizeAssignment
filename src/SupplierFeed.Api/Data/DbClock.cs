using System.Data;
using Dapper;

namespace SupplierFeed.Api.Data;

/// <summary>The database's clock: the one source of "now" for throttling and timestamp validation.</summary>
public static class DbClock
{
    private const string NowMsSql = "select cast(unixepoch('subsec') * 1000 as integer)";

    public static long NowMs(IDbConnection connection, IDbTransaction? transaction = null) =>
        connection.ExecuteScalar<long>(NowMsSql, transaction: transaction);
}
