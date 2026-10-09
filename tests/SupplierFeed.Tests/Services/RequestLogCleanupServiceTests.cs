using Dapper;
using Microsoft.Extensions.Logging;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Services;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Services;

[TestFixture]
public class RequestLogCleanupServiceTests
{
    // Timing-based by nature: a one second interval, and every wait polls up to a generous limit.
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(15);

    private TestDatabase _db = null!;
    private ListLogger<RequestLogCleanupService> _logger = null!;
    private RequestLogCleanupService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        DatabaseInitializer.Initialize(_db.Factory);
        _logger = new ListLogger<RequestLogCleanupService>();

        var cleanup = new CleanupOptions { IntervalSeconds = 1 };
        _service = new RequestLogCleanupService(
            new RequestLogCleaner(_db.Factory, new ThrottleOptions(), cleanup), cleanup, _logger);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _db.Dispose();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + GiveUpAfter;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    [Test]
    public async Task Expired_rows_disappear_on_their_own_while_in_window_rows_stay()
    {
        _db.InsertLogRows("s1", 50, ageMs: 120_000);
        _db.InsertLogRows("s1", 4, ageMs: 10_000);

        await _service.StartAsync(CancellationToken.None);

        Assert.That(await WaitUntil(() => _db.Count("request_log") == 4), Is.True, "expired rows should be cleaned within a few passes");
        Assert.That(_db.Count("request_log"), Is.EqualTo(4));
    }

    [Test]
    public async Task Rows_that_expire_later_are_cleaned_by_later_passes()
    {
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(1_500); // let at least one empty pass go by
        _db.InsertLogRows("s1", 10, ageMs: 120_000);

        Assert.That(await WaitUntil(() => _db.Count("request_log") == 0), Is.True);
    }

    [Test]
    public async Task A_failing_pass_is_logged_and_does_not_stop_the_service_which_recovers_afterwards()
    {
        _db.InsertLogRows("s1", 20, ageMs: 120_000);
        _db.Exec("create trigger fail_cleanup before delete on request_log begin select raise(abort, 'boom'); end");

        await _service.StartAsync(CancellationToken.None);

        Assert.That(await WaitUntil(() => _logger.Entries.Any(e => e.Level == LogLevel.Error)), Is.True, "the failure must be logged");
        Assert.That(_service.ExecuteTask!.IsCompleted, Is.False, "one failed pass must not stop the service (or the host)");
        Assert.That(_db.Count("request_log"), Is.EqualTo(20), "a failed pass deletes nothing");

        _db.Exec("drop trigger fail_cleanup");

        Assert.That(await WaitUntil(() => _db.Count("request_log") == 0), Is.True, "it picks up again once the cause is gone");
    }

    [Test]
    public async Task Stopping_ends_the_loop_promptly()
    {
        await _service.StartAsync(CancellationToken.None);

        var stopping = _service.StopAsync(CancellationToken.None);

        Assert.That(await Task.WhenAny(stopping, Task.Delay(5_000)), Is.SameAs(stopping));
        Assert.That(_service.ExecuteTask!.IsCompleted, Is.True);
    }
}
