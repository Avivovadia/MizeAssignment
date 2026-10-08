using SupplierFeed.Api.Data;

namespace SupplierFeed.Api.Services;

/// <summary>Reads a supplier's counters. Reading is not an ingest request: it is neither throttled nor counted.</summary>
public sealed class StatsService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly StatsStore _statsStore;

    public StatsService(SqliteConnectionFactory connectionFactory, StatsStore statsStore)
    {
        _connectionFactory = connectionFactory;
        _statsStore = statsStore;
    }

    public SupplierStats Get(string supplierId)
    {
        using var connection = _connectionFactory.Open();
        return _statsStore.Get(connection, supplierId);
    }
}
