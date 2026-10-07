namespace SupplierFeed.Api.Domain;

public static class IngestDetailExtensions
{
    /// <summary>The one rule tying a reservation outcome to the status (and stats counter) it belongs to.</summary>
    public static IngestStatus ToStatus(this IngestDetail detail) => detail switch
    {
        IngestDetail.Created or IngestDetail.Updated => IngestStatus.Ingested,
        IngestDetail.Duplicate or IngestDetail.OutOfDate => IngestStatus.Ignored,
        _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "No status is defined for this outcome."),
    };
}
