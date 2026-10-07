using SupplierFeed.Api.Domain;

namespace SupplierFeed.Tests.Domain;

[TestFixture]
public class UpdatedAtPolicyTests
{
    private const long Now = 1_800_000_000_000;

    private static UpdatedAtPolicy Policy(int skewSeconds = 300) =>
        new(new UpdatedAtOptions { MaxFutureSkewSeconds = skewSeconds });

    [Test]
    public void Timestamp_exactly_at_the_allowed_skew_is_accepted()
    {
        Assert.That(Policy(300).Check(Now + 300_000, Now), Is.Null);
    }

    [Test]
    public void Timestamp_one_millisecond_past_the_allowed_skew_is_rejected()
    {
        Assert.That(Policy(300).Check(Now + 300_001, Now), Is.Not.Null);
    }

    [Test]
    public void Past_and_present_timestamps_are_accepted()
    {
        var policy = Policy();

        Assert.That(policy.Check(Now, Now), Is.Null);
        Assert.That(policy.Check(Now - 86_400_000, Now), Is.Null);
    }

    [Test]
    public void Error_message_names_the_field_and_the_configured_skew()
    {
        var error = Policy(60).Check(Now + 61_000, Now);

        Assert.That(error, Does.Contain("updatedAtUtc"));
        Assert.That(error, Does.Contain("60"));
    }

    [Test]
    public void Zero_skew_rejects_any_future_timestamp()
    {
        var policy = Policy(0);

        Assert.That(policy.Check(Now, Now), Is.Null);
        Assert.That(policy.Check(Now + 1, Now), Is.Not.Null);
    }

    [Test]
    public void Negative_skew_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(-1));
    }

    [Test]
    public void Default_skew_is_five_minutes()
    {
        var policy = new UpdatedAtPolicy(new UpdatedAtOptions());

        Assert.That(policy.Check(Now + 300_000, Now), Is.Null);
        Assert.That(policy.Check(Now + 300_001, Now), Is.Not.Null);
    }
}
