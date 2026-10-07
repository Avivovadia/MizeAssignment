using System.Text.Json;
using SupplierFeed.Api.Domain;

namespace SupplierFeed.Tests.Domain;

[TestFixture]
public class IngestResponseJsonTests
{
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, IngestJson.Options);

    [Test]
    public void Ingested_created_uses_the_stats_vocabulary()
    {
        var json = Serialize(new IngestResponse(IngestStatus.Ingested, IngestDetail.Created));

        Assert.That(json, Is.EqualTo("{\"status\":\"ingested\",\"detail\":\"created\"}"));
    }

    [Test]
    public void Out_of_date_is_serialized_as_a_single_lowercase_word()
    {
        var json = Serialize(new IngestResponse(IngestStatus.Ignored, IngestDetail.OutOfDate));

        Assert.That(json, Is.EqualTo("{\"status\":\"ignored\",\"detail\":\"outofdate\"}"));
    }

    [Test]
    public void Throttled_response_carries_exact_retry_after_and_the_rule_that_was_exceeded()
    {
        var json = Serialize(new IngestResponse(IngestStatus.Throttled, RetryAfterMs: 29_940, Limit: 100, WindowSeconds: 60));

        Assert.That(json, Is.EqualTo(
            "{\"status\":\"throttled\",\"retryAfterMs\":29940,\"limit\":100,\"windowSeconds\":60}"));
    }

    [Test]
    public void Error_response_uses_the_invalid_status_and_lists_the_errors()
    {
        var json = Serialize(new ErrorResponse(new[] { "price must be >= 0" }));

        Assert.That(json, Is.EqualTo("{\"status\":\"invalid\",\"errors\":[\"price must be >= 0\"]}"));
    }
}
