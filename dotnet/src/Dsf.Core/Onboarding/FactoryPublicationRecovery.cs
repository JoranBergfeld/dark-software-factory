namespace Dsf.Core.Onboarding;

public enum FactoryPublicationRecoveryState { Confirmed, Rejected, NotDispatched, Partial, Blocked, Failed, Canceled, Unknown }
public sealed record FactoryPublicationRecoveryRequest(string Operation, string? Revision = null, Guid? Attempt = null);
public sealed record FactoryPublicationRetry(string Operation, string Revision, Guid Attempt);
public sealed record FactoryPublicationReceiptSummary(long RepositoryId, long IssueId, string NodeId, long Number,
    string Url, DateTimeOffset CreatedAt, string RequestId, string ResponseSha256);
public sealed record FactoryPublicationInspection(FactoryPublicationRecoveryState State, string Detail)
{
    [System.Text.Json.Serialization.JsonRequired] public int Protocol { get; init; } = 1;
    [System.Text.Json.Serialization.JsonRequired] public string Workflow { get; init; } = FactoryPublicationRecovery.Protocol;
    public Guid FactoryId { get; init; }
    public long RepositoryId { get; init; }
    public string? Product { get; init; }
    public string? Operation { get; init; }
    public string? LogicalIntent { get; init; }
    public string? Revision { get; init; }
    public Guid? Attempt { get; init; }
    public FactoryPublicationIntent? ReviewedIntent { get; init; }
    public FactoryPublicationReceiptSummary? Receipt { get; init; }
    public IReadOnlyList<FactoryPublicationAttempt> Attempts { get; init; } = [];
    public FactoryPublicationReconciliation? Reconciliation { get; init; }
    public bool AdmissionClosed { get; init; }
    public bool PublicationPermitted => false;
    public string NextActor => "Operator / original native publisher administrator";
    public string NextAction { get; init; } = "Inspect original durable response. No search adoption, replacement issue or forced retry.";
}

public static class FactoryPublicationRecovery
{
    public const string Protocol = "original-publication-outcome-v1";

    public static void Validate(FactoryPublicationRecoveryRequest request, bool change)
    {
        if (request.Operation is not { Length: 73 } || !request.Operation.StartsWith("proposal-", StringComparison.Ordinal)
            || !FactorySourceValidation.Hash(request.Operation["proposal-".Length..])
            || change && (request.Attempt is null || request.Attempt == Guid.Empty
                || request.Revision is not { Length: > 0 and <= 256 } || request.Revision.Any(char.IsControl))
            || !change && (request.Attempt is not null || request.Revision is not null))
            throw new ArgumentException("Exact original publication operation/revision/attempt required.");
    }

    public static FactoryPublicationInspection Inspect(FactoryPublicationDocument journal, AuditRecordIdentity identity, bool closed)
    {
        var state = journal.State switch
        {
            FactoryPublicationState.Confirmed => FactoryPublicationRecoveryState.Confirmed,
            FactoryPublicationState.Rejected => FactoryPublicationRecoveryState.Rejected,
            FactoryPublicationState.NotDispatched => FactoryPublicationRecoveryState.NotDispatched,
            FactoryPublicationState.Intended => FactoryPublicationRecoveryState.Partial,
            _ => FactoryPublicationRecoveryState.Unknown,
        };
        var receipt = journal.Receipt ?? journal.Response?.Receipt;
        return new(state, state == FactoryPublicationRecoveryState.Unknown
            ? journal.Response is null ? "Original issued effect unconfirmed; no native response available. Search matches prove neither ownership nor absence."
                : "Original dispatch-owned response retained; explicit conditional reconciliation can close only this original attempt."
            : "Original durable publication outcome; historical receipt grants no new admission.")
        {
            FactoryId = journal.Intent.FactoryId, RepositoryId = journal.Intent.RepositoryId, Product = journal.Product,
            Operation = journal.Id, LogicalIntent = journal.Intent.LogicalIntent, ReviewedIntent = journal.Intent,
            Revision = identity.NativeRevision, Attempt = journal.Attempts.LastOrDefault()?.Id, Attempts = journal.Attempts,
            Reconciliation = journal.Attempts.LastOrDefault()?.Reconciliation, AdmissionClosed = closed,
            Receipt = receipt is null ? null : new(receipt.RepositoryId, receipt.IssueId, receipt.NodeId, receipt.Number, receipt.Url,
                receipt.CreatedAt, receipt.RequestId, receipt.ResponseSha256),
            NextAction = state is FactoryPublicationRecoveryState.Rejected or FactoryPublicationRecoveryState.NotDispatched
                ? "Operator may explicitly retry this unchanged logical intent after restoring fresh original decision, repository automation and open admission."
                : state == FactoryPublicationRecoveryState.Confirmed ? "Original issue confirmed; no repeat, automatic edits, assignment or delivery."
                : "Operator reconciles only retained original native creation proof; otherwise keep quarantined.",
        };
    }
}
