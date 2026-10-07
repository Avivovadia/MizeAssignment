using Microsoft.Data.Sqlite;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;

namespace SupplierFeed.Api.Services;

/// <summary>
/// Runs one ingest request on a single immediate transaction. It decides nothing itself: it asks each
/// component for its result, and hands those results to the stats store.
/// Everything after the throttle admits the request runs inside a savepoint, so a failure there rolls back
/// the reservation write and the counters together while the admit row (the quota slot) stays.
/// </summary>
public sealed class IngestOrchestrator
{
    private const string ApplySavepoint = "ingest_apply";

    private static readonly string UnattributableError =
        $"{IngestFields.SupplierId} is required: the body must be a JSON object with a non-empty {IngestFields.SupplierId}";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ThrottleStore _throttleStore;
    private readonly ReservationStore _reservationStore;
    private readonly StatsStore _statsStore;
    private readonly UpdatedAtPolicy _updatedAtPolicy;
    private readonly ILogger<IngestOrchestrator> _logger;

    public IngestOrchestrator(
        SqliteConnectionFactory connectionFactory,
        ThrottleStore throttleStore,
        ReservationStore reservationStore,
        StatsStore statsStore,
        UpdatedAtPolicy updatedAtPolicy,
        ILogger<IngestOrchestrator> logger)
    {
        _connectionFactory = connectionFactory;
        _throttleStore = throttleStore;
        _reservationStore = reservationStore;
        _statsStore = statsStore;
        _updatedAtPolicy = updatedAtPolicy;
        _logger = logger;
    }

    public IngestResult Ingest(string? body)
    {
        var parsed = IngestParser.Parse(body);
        if (parsed is IngestParseResult.Unattributable)
            return new IngestResult.Unattributable(new[] { UnattributableError });

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        var (supplierId, request, invalid) = Classify(parsed, connection, transaction);

        if (_throttleStore.TryAdmit(connection, transaction, supplierId) is ThrottleDecision.Throttled throttled)
        {
            // The payload verdict and the throttle outcome are separate facts about the request, and both are
            // recorded: a supplier sending garbage must stay visible in `invalid` while it is being throttled.
            if (invalid is not null)
                _statsStore.Record(connection, transaction, supplierId, invalid);

            _statsStore.Record(connection, transaction, supplierId, throttled);
            transaction.Commit();
            return new IngestResult.Throttled(throttled);
        }

        var result = RunInSavepoint(transaction, supplierId, () => invalid is not null
            ? RecordInvalid(connection, transaction, supplierId, invalid)
            : Apply(connection, transaction, request!));

        transaction.Commit();
        return result;
    }

    /// <summary>
    /// Completes the validity verdict before the throttle. The parse validation is pure; the future-timestamp
    /// rule needs the database clock, so it is applied here and turns a valid request into an invalid one.
    /// </summary>
    private (string SupplierId, ValidatedIngest? Request, IngestParseResult.Invalid? Invalid) Classify(
        IngestParseResult parsed, SqliteConnection connection, SqliteTransaction transaction)
    {
        switch (parsed)
        {
            case IngestParseResult.Invalid invalid:
                return (invalid.SupplierId, null, invalid);

            case IngestParseResult.Valid valid:
                var request = valid.Request;
                var error = _updatedAtPolicy.Check(request.UpdatedAtMs, DbClock.NowMs(connection, transaction));
                return error is null
                    ? (request.SupplierId, request, null)
                    : (request.SupplierId, null, new IngestParseResult.Invalid(request.SupplierId, new[] { error }));

            default:
                throw new InvalidOperationException($"Unexpected parse result {parsed.GetType().Name}.");
        }
    }

    private IngestResult RecordInvalid(
        SqliteConnection connection, SqliteTransaction transaction, string supplierId, IngestParseResult.Invalid invalid)
    {
        _statsStore.Record(connection, transaction, supplierId, invalid);
        return new IngestResult.Invalid(invalid.Errors);
    }

    private IngestResult Apply(SqliteConnection connection, SqliteTransaction transaction, ValidatedIngest request)
    {
        var detail = _reservationStore.Apply(connection, transaction, request);
        _statsStore.Record(connection, transaction, request.SupplierId, detail);
        return new IngestResult.Processed(detail);
    }

    private IngestResult RunInSavepoint(SqliteTransaction transaction, string supplierId, Func<IngestResult> work)
    {
        transaction.Save(ApplySavepoint);
        try
        {
            var result = work();
            transaction.Release(ApplySavepoint);
            return result;
        }
        catch (Exception exception)
        {
            transaction.Rollback(ApplySavepoint);
            transaction.Release(ApplySavepoint);
            _logger.LogError(exception, "Applying an admitted request for supplier {SupplierId} failed; its quota slot is kept", supplierId);
            return new IngestResult.Failed();
        }
    }
}
