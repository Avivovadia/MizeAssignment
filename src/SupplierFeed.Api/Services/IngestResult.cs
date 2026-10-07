using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;

namespace SupplierFeed.Api.Services;

/// <summary>What the orchestrator did with one ingest request; the HTTP layer maps each case to a response.</summary>
public abstract record IngestResult
{
    private IngestResult() { }

    /// <summary>No usable supplierId: nothing was throttled, counted or written.</summary>
    public sealed record Unattributable(IReadOnlyList<string> Errors) : IngestResult;

    /// <summary>Admitted (counts toward the limit) but rejected by validation.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : IngestResult;

    public sealed record Throttled(ThrottleDecision.Throttled Decision) : IngestResult;

    public sealed record Processed(IngestDetail Detail) : IngestResult
    {
        public IngestStatus Status => Detail.ToStatus();
    }

    /// <summary>Admitted, but applying it failed; the quota slot stays consumed and nothing else changed.</summary>
    public sealed record Failed : IngestResult;
}
