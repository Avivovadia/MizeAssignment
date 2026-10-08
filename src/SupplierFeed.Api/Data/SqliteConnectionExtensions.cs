using Microsoft.Data.Sqlite;

namespace SupplierFeed.Api.Data;

public static class SqliteConnectionExtensions
{
    /// <summary>
    /// Starts a transaction that takes the write lock immediately (BEGIN IMMEDIATE), before any statement runs.
    /// The throttle, reservation and stats stores read and then write on the caller's transaction and rely on
    /// no other writer slipping in between, so every request transaction must be started with this.
    /// A deferred transaction only locks at its first write, and its snapshot can then be stale.
    /// </summary>
    public static SqliteTransaction BeginWriteTransaction(this SqliteConnection connection) =>
        connection.BeginTransaction(deferred: false);
}
