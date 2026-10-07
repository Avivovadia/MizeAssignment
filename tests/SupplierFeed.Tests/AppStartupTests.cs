using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SupplierFeed.Api.Data;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests;

[TestFixture]
public class AppStartupTests
{
    private TestDatabase _db = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", _db.ConnectionString));
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Test]
    public async Task App_Starts_And_Unknown_Route_Returns_404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/does-not-exist");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void Throttle_options_are_bound_from_configuration()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", _db.ConnectionString);
            b.UseSetting("Throttle:Limit", "2");
        });
        var store = factory.Services.GetRequiredService<ThrottleStore>();

        using var connection = _db.Factory.Open();
        using var transaction = connection.BeginTransaction();
        var decisions = Enumerable.Range(0, 3).Select(_ => store.TryAdmit(connection, transaction, "config-test")).ToList();

        Assert.That(decisions.OfType<ThrottleDecision.Admitted>().Count(), Is.EqualTo(2));
        Assert.That(decisions[2], Is.InstanceOf<ThrottleDecision.Throttled>());
    }

    [Test]
    public void App_Startup_Creates_Schema_In_The_Configured_Database()
    {
        using var client = _factory.CreateClient();

        using var conn = _db.Factory.Open();
        var tables = conn.Query<string>("select name from sqlite_master where type = 'table'").ToList();

        Assert.That(tables, Is.SupersetOf(new[] { "reservations", "request_log", "supplier_stats" }));
    }
}
