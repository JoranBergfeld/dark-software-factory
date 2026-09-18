using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryPauseState { NotRequested, DrainingUnknown, Partial, Blocked, Failed, Canceled, Unknown }

public sealed record FactoryPauseRequest(int Protocol, string Kind, Guid OperationId, Guid FactoryId, long RepositoryId,
    string ProfilePin, string ManifestRevision, string ManifestSha256, FactoryManifest ReviewedManifest,
    string RequestedBy, DateTimeOffset RequestedAt);

public sealed record FactoryPauseResult(FactoryPauseState State, string Detail)
{
    public FactoryPauseRequest? Request { get; init; }
    public bool? AdmissionClosed { get; init; }
    public bool StopCompleted => false;
    public bool IsActivated => false;
    public string NextAction => AdmissionClosed == true
        ? "Inspect retained work and issued effects. No automatic restart, takeover, rollback or completed-stop claim."
        : "Pause inspection grants no work or publication authority.";
}

/// <summary>An operator-owned stop request subtracts authority; it never proves remote work has stopped.</summary>
public static class FactoryPause
{
    public const string Key = "dsf/factory/pause";
    public const string HistoryPrefix = FactoryAudit.Namespace + "pause/";
    public const string Kind = "decide-pause-request";
    public static string HistoryKey(Guid operation) => HistoryPrefix + operation.ToString("N") + "/request";

    public static FactoryPauseRequest Read(string value, FactoryManifest manifest)
    {
        var request = JsonSerializer.Deserialize<FactoryPauseRequest>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var reviewed = request.ReviewedManifest;
        if (request.Protocol != 1 || request.Kind != Kind || request.OperationId == Guid.Empty
            || request.FactoryId != manifest.Identity.FactoryId || request.RepositoryId != manifest.Identity.RepositoryId
            || reviewed.Identity.FactoryId != request.FactoryId || reviewed.Identity.RepositoryId != request.RepositoryId
            || reviewed.Identity.ProductKey != manifest.Identity.ProductKey || reviewed.Identity.OwnerEndpoint != manifest.Identity.OwnerEndpoint
            || reviewed.Identity.TenantId != manifest.Identity.TenantId || reviewed.Identity.SubscriptionId != manifest.Identity.SubscriptionId
            || request.ProfilePin != reviewed.ProfilePin || !FactorySourceValidation.Hash(request.ProfilePin)
            || request.ManifestRevision.Length is < 1 or > 256 || request.ManifestSha256 != FactoryModelsValidation.Hash(reviewed)
            || request.RequestedBy != reviewed.Identity.AzureActorId || !Guid.TryParse(request.RequestedBy, out var actor) || actor == Guid.Empty
            || request.RequestedAt.Offset != TimeSpan.Zero || request.RequestedAt < reviewed.Identity.CreatedAt)
            throw new JsonException("Protected pause request binding is invalid.");
        return request;
    }

    public static void RequireAdmissionOpen(IEnumerable<string> keys)
    {
        // A corrupt root or surviving history can only remove permission, never recreate unpaused authority.
        if (keys.Any(key => key.StartsWith(Key, StringComparison.Ordinal) || key.StartsWith(HistoryPrefix, StringComparison.Ordinal)))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "DeliberatePauseOrUnconfirmedStop: admission closed; issued effects remain draining or unknown.");
    }
}
