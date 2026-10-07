using Microsoft.Data.Sqlite;

namespace SupplierFeed.Api.Data;

/// <summary>Opens connections with the settings the throttle design relies on.</summary>
public sealed class SqliteConnectionFactory
{
    // Default Timeout is how long a writer waits on a held lock (seconds). 0 means wait forever.
    private const int FallbackTimeoutSeconds = 15;

    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.DefaultTimeout <= 0)
            builder.DefaultTimeout = FallbackTimeoutSeconds;
        _connectionString = builder.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
