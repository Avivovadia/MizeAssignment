namespace SupplierFeed.Api.Domain;

public sealed class UpdatedAtOptions
{
    public const string SectionName = "UpdatedAt";

    /// <summary>How far ahead of server time an updatedAtUtc may be (covers supplier clock drift).</summary>
    public int MaxFutureSkewSeconds { get; init; } = 300;
}
