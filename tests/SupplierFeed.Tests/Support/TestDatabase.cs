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
