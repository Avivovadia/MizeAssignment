using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SupplierFeed.Tests;

[TestFixture]
public class AppStartupTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void SetUp() => _factory = new WebApplicationFactory<Program>();

    [OneTimeTearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task App_Starts_And_Unknown_Route_Returns_404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/does-not-exist");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
