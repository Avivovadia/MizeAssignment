using SupplierFeed.Api.Data;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class DbClockTests
{
    private TestDatabase _db = null!;

    [SetUp]
    public void SetUp() => _db = new TestDatabase();

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void Now_is_close_to_the_process_clock_in_epoch_milliseconds()
    {
        using var connection = _db.Factory.Open();

        var now = DbClock.NowMs(connection);

        Assert.That(now, Is.EqualTo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Within(5_000));
    }

    [Test]
    public void Now_never_goes_backwards_and_works_inside_a_transaction()
    {
        using var connection = _db.Factory.Open();
        using var transaction = connection.BeginTransaction();

        var first = DbClock.NowMs(connection, transaction);
        Thread.Sleep(5);
        var second = DbClock.NowMs(connection, transaction);

        Assert.That(second, Is.GreaterThan(first));
    }
}
