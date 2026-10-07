using Dapper;
using Microsoft.Data.Sqlite;
using SupplierFeed.Api.Data;

namespace SupplierFeed.Tests.Support;

/// <summary>A throwaway SQLite file per test fixture, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"supplierfeed-test-{Guid.NewGuid():N}.db");

    public TestDatabase() => Factory = new SqliteConnectionFactory(ConnectionString);

    public string ConnectionString => $"Data Source={_path}";

    public SqliteConnectionFactory Factory { get; }

    /// <summary>Inserts request_log rows stamped <paramref name="ageMs"/> before the DB's current time.</summary>
    public void InsertLogRows(string supplierId, int count, long ageMs)
    {
        using var connection = Factory.Open();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            connection.Execute(
                "insert into request_log (supplierId, tsMs) values (@supplierId, cast(unixepoch('subsec') * 1000 as integer) - @ageMs)",
                new { supplierId, ageMs },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>Inserts request_log rows at an exact timestamp, for tests that fix the clock.</summary>
    public void InsertLogRowsAt(string supplierId, int count, long tsMs)
    {
        using var connection = Factory.Open();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
            connection.Execute("insert into request_log (supplierId, tsMs) values (@supplierId, @tsMs)", new { supplierId, tsMs }, transaction);

        transaction.Commit();
    }

    public long LogCount(string supplierId)
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<long>("select count(*) from request_log where supplierId = @supplierId", new { supplierId });
    }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { }
        }
    }
}
