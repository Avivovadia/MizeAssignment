using SupplierFeed.Api.Data;

namespace SupplierFeed.Api.Services;

/// <summary>
/// Runs the cleaner periodically in every instance. It is idempotent, so overlapping instances are harmless;
/// an external cleaner (a cron job or a database-side scheduler) running once for all instances would be
/// better, but SQLite has neither TTL nor a scheduler (see the README).
/// </summary>
public sealed class RequestLogCleanupService : BackgroundService
{
    private readonly RequestLogCleaner _cleaner;
    private readonly CleanupOptions _options;
    private readonly ILogger<RequestLogCleanupService> _logger;

    public RequestLogCleanupService(RequestLogCleaner cleaner, CleanupOptions options, ILogger<RequestLogCleanupService> logger)
    {
        options.Validate();
        _cleaner = cleaner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        try
        {
            // A random first delay, so instances started together don't all clean at the same moment.
            await Task.Delay(interval * Random.Shared.NextDouble(), stoppingToken);

            using var timer = new PeriodicTimer(interval);
            do
            {
                RunOnePass();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void RunOnePass()
    {
        try
        {
            var deleted = _cleaner.Run();
            _logger.LogDebug("Request log cleanup removed {Deleted} expired rows", deleted);
        }
        catch (Exception exception)
        {
            // A failed pass must not stop the host (an unhandled exception in a BackgroundService does); the
            // next pass simply tries again.
            _logger.LogError(exception, "Request log cleanup failed; it will be retried on the next pass");
        }
    }
}
