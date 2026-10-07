namespace SupplierFeed.Api.Data;

/// <summary>At most <see cref="Limit"/> admitted requests per supplier in any rolling <see cref="WindowSeconds"/>.</summary>
public sealed class ThrottleOptions
{
    public const string SectionName = "Throttle";

    public int Limit { get; init; } = 100;

    public int WindowSeconds { get; init; } = 60;
}
