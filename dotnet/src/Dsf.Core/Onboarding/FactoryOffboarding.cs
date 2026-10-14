using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryOffboardingState
{
    ReviewRequired, Detaching, Partial, Detached, Blocked, Failed, Canceled, Unknown,
}

public sealed record FactoryOffboardingPlan(
    int Protocol, string Kind, Guid OperationId, Guid FactoryId, string ProductKey,
    string AuthorityRevision, IReadOnlyList<string> PreservedAssets,
    IReadOnlyList<string> ProposedRemovals, IReadOnlyList<string> OwnershipBlockers,
    IReadOnlyList<string> ContinuingCosts, IReadOnlyList<string> ResidualAuthority,
    string Fingerprint);

public sealed record FactoryOffboardingRequest(
    int Protocol, string Kind, Guid OperationId, Guid FactoryId, string PlanFingerprint,
    string AuthorityRevision, string ApprovedBy, DateTimeOffset RequestedAt);

public sealed record FactoryOffboardingPointer(
    Guid OperationId, FactoryOffboardingState State, string PlanFingerprint, string AuthorityRevision,
    DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt = null);

public sealed record FactoryOffboardingResult(FactoryOffboardingState State, string Detail)
{
    public FactoryOffboardingPlan? Plan { get; init; }
    public FactoryOffboardingRequest? Request { get; init; }
    public bool AdmissionClosed { get; init; }
    public bool StopCompleted => State == FactoryOffboardingState.Detached;
    public bool RegistrationRetained { get; init; }
    public bool InfrastructurePurged => false;
    public bool IdentityRetired => false;
    public IReadOnlyList<string> CompletedRemovals { get; init; } = [];
    public IReadOnlyList<string> BlockedRemovals { get; init; } = [];
    public string NextAction => State switch
    {
        FactoryOffboardingState.ReviewRequired => "Review exact preserved assets, removals, blockers, costs and residual authority; approve only this fingerprint.",
        FactoryOffboardingState.Detached => "Retain audit and disabled infrastructure; separately review reattachment or destructive purge.",
        _ => "Inspect the original detach operation and current provider state; finish safe independent cleanup without force, replay or a completed-stop claim.",
    };
}

public static class FactoryOffboarding
{
    public const string Key = "dsf/factory/offboarding";
    public const string HistoryPrefix = FactoryAudit.Namespace + "offboarding/";
    public const string Kind = "decide-offboarding-request";
    public static string HistoryKey(Guid operation) => HistoryPrefix + operation.ToString("N") + "/request";

    public static string Fingerprint(FactoryOffboardingPlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan with { Fingerprint = "" }, FactoryGitHubJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static FactoryOffboardingRequest Read(string value, FactoryReservationIdentity identity)
    {
        var request = JsonSerializer.Deserialize<FactoryOffboardingRequest>(value, FactoryGitHubJson.Options)
            ?? throw new JsonException();
        if (request.Protocol != 1 || request.Kind != Kind || request.OperationId == Guid.Empty
            || request.FactoryId != identity.FactoryId || request.PlanFingerprint.Length != 64
            || request.PlanFingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || string.IsNullOrWhiteSpace(request.AuthorityRevision) || request.AuthorityRevision.Length > 256
            || request.ApprovedBy != identity.AzureActorId || request.RequestedAt.Offset != TimeSpan.Zero
            || request.RequestedAt < identity.CreatedAt)
            throw new JsonException("Protected offboarding request binding is invalid.");
        return request;
    }

    public static bool IsStopRecord(string key) =>
        key.StartsWith(Key, StringComparison.Ordinal) || key.StartsWith(HistoryPrefix, StringComparison.Ordinal);

    public static string RedactedDetail =>
        "Council detached; disabled factory infrastructure, identity, audit and recovery remain and may continue costing money. "
        + "Shared owner key/App installation and repository-wide access were not revoked; other key copies and already-issued tokens may remain valid.";
}
