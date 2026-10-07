namespace SupplierFeed.Api.Domain;

/// <summary>Outcome of reading an ingest body. Only Unattributable skips the throttle.</summary>
public abstract record IngestParseResult
{
    private IngestParseResult() { }

    /// <summary>No usable supplierId, so there is nothing to throttle by.</summary>
    public sealed record Unattributable : IngestParseResult;

    /// <summary>The supplier is known (counts toward its limit) but the payload is not acceptable.</summary>
    public sealed record Invalid(string SupplierId, IReadOnlyList<string> Errors) : IngestParseResult;

    public sealed record Valid(ValidatedIngest Request) : IngestParseResult;
}
