using SupplierFeed.Api.Domain;

namespace SupplierFeed.Tests.Domain;

[TestFixture]
public class IngestDetailExtensionsTests
{
    [TestCase(IngestDetail.Created, IngestStatus.Ingested)]
    [TestCase(IngestDetail.Updated, IngestStatus.Ingested)]
    [TestCase(IngestDetail.Duplicate, IngestStatus.Ignored)]
    [TestCase(IngestDetail.OutOfDate, IngestStatus.Ignored)]
    public void Detail_maps_to_the_status_that_matches_its_stats_counter(IngestDetail detail, IngestStatus expected)
    {
        Assert.That(detail.ToStatus(), Is.EqualTo(expected));
    }

    [Test]
    public void An_unknown_detail_value_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((IngestDetail)99).ToStatus());
    }
}
