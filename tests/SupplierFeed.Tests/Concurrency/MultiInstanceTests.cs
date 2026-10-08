using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Concurrency;

/// <summary>
/// Several server instances sharing one database file, hit at the same time. The limit must hold across
/// all of them together, which is the whole point of keeping the window in the database.
/// Scope: instances on one machine. SQLite cannot safely span machines, so different-machine behavior
/// (and clock skew between machines) is not tested; see the limitations in the README.
/// </summary>
[TestFixture]
public class MultiInstanceTests
{
    private const int Instances = 3;
    private const int Requests = 150;
    private const int Limit = 100;
    private const int Callers = 32;

    private TestDatabase _db = null!;

    [SetUp]
    public void SetUp() => _db = new TestDatabase();

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) =>
        client.PostAsync("/api/reservations/ingest", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    private static async Task<SupplierStatsDto> GetStatsAsync(HttpClient client, string supplierId) =>
        (await client.GetFromJsonAsync<SupplierStatsDto>($"/api/reservations/stats/{supplierId}"))!;

    private sealed record SupplierStatsDto(string SupplierId, long Ingested, long Ignored, long Invalid, long Throttled);

    private static void AssertExactlyTheLimitWasAdmitted(Timed<HttpStatusCode>[] calls)
    {
        var statuses = calls.Select(c => c.Result).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        Assert.That(statuses.GetValueOrDefault(HttpStatusCode.Created), Is.EqualTo(Limit), "created");
        Assert.That(statuses.GetValueOrDefault((HttpStatusCode)429), Is.EqualTo(Requests - Limit), "throttled");
        Assert.That(statuses.Keys.Where(s => (int)s >= 500), Is.Empty, "no request may fail because of lock contention");
    }

    // ---- layer 2: several hosts in this process, each with its own startup, pools and connections ----

    [Test, Repeat(3)]
    public async Task Three_hosts_sharing_one_database_admit_exactly_the_limit_between_them()
    {
        var hosts = Enumerable.Range(0, Instances)
            .Select(_ => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", _db.ConnectionString)))
            .ToList();
        var clients = hosts.Select(h => h.CreateClient()).ToList();
        try
        {
            var calls = await Load.RunAsync(Requests, Callers, async i =>
                (await PostAsync(clients[i % Instances], TestBodies.Valid("s1", $"r{i}"))).StatusCode);

            AssertExactlyTheLimitWasAdmitted(calls);
            Assert.That(_db.Count("request_log"), Is.EqualTo(Limit));
            Assert.That(_db.Count("reservations"), Is.EqualTo(Limit));

            // Whichever instance you ask, the counters agree.
            foreach (var client in clients)
                Assert.That(await GetStatsAsync(client, "s1"), Is.EqualTo(new SupplierStatsDto("s1", Limit, 0, 0, Requests - Limit)));
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
            hosts.ForEach(h => h.Dispose());
        }
    }

    [Test, Repeat(3)]
    public async Task Versions_of_one_reservation_sent_to_different_hosts_never_regress_the_stored_data()
    {
        const int versions = 60;
        var hosts = Enumerable.Range(0, Instances)
            .Select(_ => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", _db.ConnectionString)))
            .ToList();
        var clients = hosts.Select(h => h.CreateClient()).ToList();
        var order = Enumerable.Range(1, versions).OrderBy(_ => Random.Shared.Next()).ToArray();
        var baseTime = DateTimeOffset.Parse(TestBodies.DefaultUpdatedAt);
        try
        {
            var calls = await Load.RunAsync(versions, Callers, async i =>
            {
                var body = TestBodies.Valid("s1", "r1", updatedAt: baseTime.AddMinutes(order[i]).ToString("O"));
                body["roomId"] = $"room-{order[i]}";
                return (await PostAsync(clients[i % Instances], body)).StatusCode;
            });

            Assert.That(calls.Select(c => c.Result), Is.All.AnyOf(HttpStatusCode.Created, HttpStatusCode.OK));
            Assert.That(calls.Count(c => c.Result == HttpStatusCode.Created), Is.EqualTo(1), "created exactly once");
            using var connection = _db.Factory.Open();
            Assert.That(Dapper.SqlMapper.ExecuteScalar<string>(connection, "select roomId from reservations"), Is.EqualTo($"room-{versions}"));
            Assert.That(_db.Count("reservations"), Is.EqualTo(1));
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
            hosts.ForEach(h => h.Dispose());
        }
    }

    // ---- layer 3: real separate OS processes, sharing nothing but the file and its locks ----

    private async Task<(ServerProcess[] Servers, HttpClient[] Clients)> StartProcessesAsync(int count, params (string Key, string Value)[] settings)
    {
        // Started at the same moment on purpose: instances are deployed together, and they all create the schema.
        var servers = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => ServerProcess.StartAsync(_db.ConnectionString, settings)));
        return (servers, servers.Select(s => s.CreateClient()).ToArray());
    }

    private static void Stop(ServerProcess[] servers, HttpClient[] clients)
    {
        foreach (var client in clients) client.Dispose();
        foreach (var server in servers) server.Dispose();
    }

    [Test, Category("MultiProcess")]
    public async Task Two_real_server_processes_sharing_one_database_admit_exactly_the_limit_between_them()
    {
        var (servers, clients) = await StartProcessesAsync(2);
        try
        {
            var calls = await Load.RunAsync(Requests, Callers, async i =>
                (await PostAsync(clients[i % clients.Length], TestBodies.Valid("s1", $"r{i}"))).StatusCode);

            AssertExactlyTheLimitWasAdmitted(calls);
            Assert.That(_db.Count("request_log"), Is.EqualTo(Limit));
            foreach (var client in clients)
                Assert.That(await GetStatsAsync(client, "s1"), Is.EqualTo(new SupplierStatsDto("s1", Limit, 0, 0, Requests - Limit)));
        }
        finally
        {
            Stop(servers, clients);
        }
    }

    [Test, Category("MultiProcess")]
    public async Task Versions_of_one_reservation_sent_to_different_processes_never_regress_the_stored_data()
    {
        const int versions = 60;
        var (servers, clients) = await StartProcessesAsync(2);
        var order = Enumerable.Range(1, versions).OrderBy(_ => Random.Shared.Next()).ToArray();
        var baseTime = DateTimeOffset.Parse(TestBodies.DefaultUpdatedAt);
        try
        {
            var calls = await Load.RunAsync(versions, Callers, async i =>
            {
                var body = TestBodies.Valid("s1", "r1", updatedAt: baseTime.AddMinutes(order[i]).ToString("O"));
                body["roomId"] = $"room-{order[i]}";
                return (await PostAsync(clients[i % clients.Length], body)).StatusCode;
            });

            Assert.That(calls.Select(c => c.Result), Is.All.AnyOf(HttpStatusCode.Created, HttpStatusCode.OK));
            Assert.That(calls.Count(c => c.Result == HttpStatusCode.Created), Is.EqualTo(1), "created exactly once");
            using var connection = _db.Factory.Open();
            Assert.That(Dapper.SqlMapper.ExecuteScalar<string>(connection, "select roomId from reservations"), Is.EqualTo($"room-{versions}"));
        }
        finally
        {
            Stop(servers, clients);
        }
    }
}
