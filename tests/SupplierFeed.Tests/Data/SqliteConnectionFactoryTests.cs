using SupplierFeed.Api.Data;

namespace SupplierFeed.Tests.Data;

[TestFixture]
public class SqliteConnectionFactoryTests
{
    [TestCase("Data Source=:memory:;Default Timeout=0")]
    [TestCase("Data Source=:memory:")]
    public void Open_always_uses_a_finite_lock_timeout(string connectionString)
    {
        // Default Timeout=0 means "wait forever" for Microsoft.Data.Sqlite.
        using var conn = new SqliteConnectionFactory(connectionString).Open();

        Assert.That(conn.DefaultTimeout, Is.GreaterThan(0));
    }

    [Test]
    public void Open_keeps_an_explicit_positive_timeout()
    {
        using var conn = new SqliteConnectionFactory("Data Source=:memory:;Default Timeout=7").Open();

        Assert.That(conn.DefaultTimeout, Is.EqualTo(7));
    }
}
