using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Api;

[TestFixture]
public class ReservationsApiTests
{
    private const string IngestUrl = "/api/reservations/ingest";

    private TestDatabase _db = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp() => Start();

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        _db.Dispose();
    }

    private void Start(params (string Key, string Value)[] settings)
    {
        _db = new TestDatabase();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", _db.ConnectionString);
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });
        _client = _factory.CreateClient();
    }

    private async Task<(HttpResponseMessage Response, string Body)> Post(
        string json, string contentType = "application/json")
    {
        using var content = new StringContent(json, Encoding.UTF8, contentType);
        var response = await _client.PostAsync(IngestUrl, content);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private Task<(HttpResponseMessage Response, string Body)> Post(JsonObject body) => Post(body.ToJsonString());

    private async Task<string> GetStats(string supplierId)
    {
        var response = await _client.GetAsync($"/api/reservations/stats/{Uri.EscapeDataString(supplierId)}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    // ---- reservation outcomes: exact status codes and exact JSON ----

    [Test]
    public async Task New_reservation_returns_201_ingested_created()
    {
        var (response, body) = await Post(TestBodies.Valid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(body, Is.EqualTo("{\"status\":\"ingested\",\"detail\":\"created\"}"));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
    }

    [Test]
    public async Task Changed_reservation_returns_200_ingested_updated()
    {
        await Post(TestBodies.Valid());
        var changed = TestBodies.Valid(updatedAt: TestBodies.LaterUpdatedAt);
        changed["roomId"] = "room-9";

        var (response, body) = await Post(changed);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Is.EqualTo("{\"status\":\"ingested\",\"detail\":\"updated\"}"));
    }

    [Test]
    public async Task Resent_reservation_returns_200_ignored_duplicate()
    {
        await Post(TestBodies.Valid());

        var (response, body) = await Post(TestBodies.Valid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Is.EqualTo("{\"status\":\"ignored\",\"detail\":\"duplicate\"}"));
    }

    [Test]
    public async Task Delayed_older_update_returns_200_ignored_outofdate()
    {
        await Post(TestBodies.Valid(updatedAt: TestBodies.LaterUpdatedAt));
        var delayed = TestBodies.Valid(updatedAt: TestBodies.EarlierUpdatedAt);
        delayed["roomId"] = "room-9";

        var (response, body) = await Post(delayed);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Is.EqualTo("{\"status\":\"ignored\",\"detail\":\"outofdate\"}"));
    }

    // ---- 400: invalid and unattributable ----

    [Test]
    public async Task Invalid_payload_returns_400_with_status_invalid_and_the_errors()
    {
        var (response, body) = await Post(TestBodies.Invalid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = JsonNode.Parse(body)!;
        Assert.That((string?)json["status"], Is.EqualTo("invalid"));
        Assert.That(json["errors"]!.AsArray().Single()!.ToString(), Does.Contain("price"));
        Assert.That(_db.Count("reservations"), Is.Zero);
        Assert.That(_db.Count("request_log"), Is.EqualTo(1), "invalid requests count toward the limit");
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{\"supplierId\":\"   \"}")]
    public async Task Body_without_a_usable_supplier_returns_400_and_touches_no_table(string body)
    {
        var (response, responseBody) = await Post(body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((string?)JsonNode.Parse(responseBody)!["status"], Is.EqualTo("invalid"));
        Assert.That(_db.Count("request_log"), Is.Zero);
        Assert.That(_db.Count("supplier_stats"), Is.Zero);
    }

    [Test]
    public async Task Content_type_is_ignored_so_a_valid_body_sent_as_text_plain_still_works()
    {
        var (response, _) = await Post(TestBodies.Valid().ToJsonString(), contentType: "text/plain");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    // ---- 429 ----

    [Test]
    public async Task Request_over_the_limit_returns_429_with_retry_after_and_the_configured_rule()
    {
        _client.Dispose(); _factory.Dispose(); _db.Dispose();
        Start(("Throttle:Limit", "2"));

        await Post(TestBodies.Valid(reservationId: "r1"));
        await Post(TestBodies.Valid(reservationId: "r2"));
        var (response, body) = await Post(TestBodies.Valid(reservationId: "r3"));

        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)429));
        var json = JsonNode.Parse(body)!;
        Assert.That((string?)json["status"], Is.EqualTo("throttled"));
        Assert.That((int?)json["limit"], Is.EqualTo(2));
        Assert.That((int?)json["windowSeconds"], Is.EqualTo(60));
        var retryAfterMs = (long)json["retryAfterMs"]!;
        Assert.That(retryAfterMs, Is.InRange(1, 60_000));

        // The header is whole seconds (HTTP allows nothing finer), rounded up from the exact milliseconds.
        var header = response.Headers.GetValues("Retry-After").Single();
        Assert.That(long.Parse(header), Is.EqualTo((retryAfterMs + 999) / 1000));
        Assert.That(_db.Count("reservations"), Is.EqualTo(2));
    }

    [Test]
    public async Task Invalid_payload_that_is_throttled_returns_429_not_400()
    {
        _client.Dispose(); _factory.Dispose(); _db.Dispose();
        Start(("Throttle:Limit", "2"));
        await Post(TestBodies.Valid(reservationId: "r1"));
        await Post(TestBodies.Valid(reservationId: "r2"));

        var (response, body) = await Post(TestBodies.Invalid());

        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)429));
        Assert.That((string?)JsonNode.Parse(body)!["status"], Is.EqualTo("throttled"));
        Assert.That(await GetStats("s1"), Is.EqualTo(
            "{\"supplierId\":\"s1\",\"ingested\":2,\"ignored\":0,\"invalid\":1,\"throttled\":1}"));
    }

    // ---- 500 ----

    [Test]
    public async Task Failure_while_applying_returns_500_and_still_consumes_the_quota_slot()
    {
        _db.Exec("create trigger fail_reservations before insert on reservations begin select raise(abort, 'boom'); end");

        var (response, _) = await Post(TestBodies.Valid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(_db.Count("request_log"), Is.EqualTo(1));
        Assert.That(_db.Count("reservations"), Is.Zero);
    }

    // ---- stats ----

    [Test]
    public async Task Unknown_supplier_has_all_zero_stats()
    {
        Assert.That(await GetStats("nobody"), Is.EqualTo(
            "{\"supplierId\":\"nobody\",\"ingested\":0,\"ignored\":0,\"invalid\":0,\"throttled\":0}"));
    }

    [Test]
    public async Task Stats_match_what_happened_across_a_mixed_sequence()
    {
        await Post(TestBodies.Valid());                 // created
        await Post(TestBodies.Valid());                 // duplicate
        await Post(TestBodies.Invalid());               // invalid
        await Post(TestBodies.Valid("other"));          // another supplier, must not leak in

        Assert.That(await GetStats("s1"), Is.EqualTo(
            "{\"supplierId\":\"s1\",\"ingested\":1,\"ignored\":1,\"invalid\":1,\"throttled\":0}"));
    }

    [Test]
    public async Task Reading_stats_is_not_counted_and_does_not_use_the_quota()
    {
        for (var i = 0; i < 5; i++)
            await GetStats("s1");

        Assert.That(_db.Count("request_log"), Is.Zero);
        Assert.That(_db.Count("supplier_stats"), Is.Zero);
    }

    [Test]
    public async Task Supplier_ids_with_spaces_work_end_to_end()
    {
        await Post(TestBodies.Valid("supplier one"));

        Assert.That(await GetStats("supplier one"), Does.Contain("\"ingested\":1"));
    }

    // ---- routing ----

    [Test]
    public async Task Wrong_http_methods_are_rejected_with_405()
    {
        Assert.That((await _client.GetAsync(IngestUrl)).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));

        using var empty = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.That((await _client.PostAsync("/api/reservations/stats/s1", empty)).StatusCode,
            Is.EqualTo(HttpStatusCode.MethodNotAllowed));
    }
}
