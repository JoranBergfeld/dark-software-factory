using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum TelemetryState { Ready, Partial, Blocked, Failed, Canceled, Unknown }
public enum TelemetryReason
{
    None, Empty, Stale, PartialResponse, WrongScope, Unsupported, AmbiguousAuthority,
    PermissionDenied, NetworkUnavailable, MalformedResponse, AuthorityChanged,
    SensitiveContent,
}

public sealed record TelemetryBackend(
    string ResourceId, string WorkspaceId, IReadOnlyList<string> Tables, IReadOnlyList<string> ApplicationResourceIds);

public sealed record TelemetryAccessGrant(string Target, string PrincipalId, string Scope, string RoleDefinitionId);

public sealed record FactoryTelemetryConfiguration(
    int SchemaVersion, Guid FactoryId, string PlanFingerprint, string ApplicationEnvironment,
    IReadOnlyList<string> ApplicationResourceIds, IReadOnlyList<TelemetryBackend> Backends,
    FoundationOperation SourceIdentity, FoundationOperation HostingEnvironment,
    string ProductEndpoint, string RuntimeImage, string AudienceClientId, int FreshnessSeconds, string QueryRevision)
{
    public string? HostTemplateSha256 { get; init; }
    public IReadOnlyList<TelemetryAccessGrant> AccessGrants { get; init; } = [];
    public IReadOnlyDictionary<string, string> Queries { get; init; } = Backends.SelectMany(b => b.Tables)
        .Distinct(StringComparer.Ordinal).ToDictionary(t => t, FactoryTelemetryValidation.Query, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> StaleQueries { get; init; } = Backends.SelectMany(b => b.Tables)
        .Distinct(StringComparer.Ordinal).ToDictionary(t => t, FactoryTelemetryValidation.StaleQuery, StringComparer.Ordinal);
    public string CoverageLimitations { get; init; } = FactoryTelemetryValidation.Coverage;
}

public sealed record FactoryTelemetry(
    int SchemaVersion, Guid OperationId, string ApprovalFingerprint, FactoryTelemetryConfiguration Configuration,
    TelemetryState State, string Stage)
{
    public string Detail { get; init; } = "Read-only preview; approve exact source, grants, query and positive freshness window.";
    public string? HostEndpoint { get; init; }
    public int OperationCount { get; init; }
    public IReadOnlyDictionary<string, string> EffectHashes { get; init; } = new Dictionary<string, string>();
    public TelemetryProof? Proof { get; init; }
    public TelemetryReason LastFailure { get; init; }
    public Guid? CheckId { get; init; }
    public string? LastCheckReference { get; init; }
    public string? LastCheckSha256 { get; init; }
    public string? VerifiedProofReference { get; init; }
    public string? VerifiedProofSha256 { get; init; }
}

public sealed record TelemetryRecordReference(
    string BackendResourceId, string WorkspaceId, string Table, string ApplicationResourceId,
    DateTimeOffset RecordTime, string ItemId, int ObservedRecords);

public sealed record TelemetryQueryObservation(
    string BackendResourceId, string Table, TelemetryReason Reason, string QuerySha256,
    string? ProviderRequestId, int Records, TelemetryRecordReference? LatestRecord);

public sealed record TelemetryProof(
    TelemetryState State, TelemetryReason Reason, Guid FactoryId, Guid OperationId,
    string ConfigurationRevision, string SourcePrincipalId, string ApplicationEnvironment,
    DateTimeOffset ObservedAt, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    IReadOnlyList<TelemetryRecordReference> Records, string Detail)
{
    public IReadOnlyList<TelemetryQueryObservation> Queries { get; init; } = [];
    public Guid? CheckId { get; init; }
    public string AttributionBasis { get; init; } = FactoryTelemetryValidation.Attribution;
    public string CoverageLimitations { get; init; } = FactoryTelemetryValidation.Coverage;
}

public static class FactoryTelemetryValidation
{
    public const string BackendApiVersion = "2026-03-01";
    public const string QueryRevision = "azuremonitor-workspace-native-v1";
    public const string Attribution = "Azure Monitor native _ResourceId mapped to explicitly selected workspace-based Application Insights components; reviewed dedicated environment.";
    public const string Coverage = "At least one real record is required per selected backend; not every component or code path is thereby observed. Empty or stale telemetry is NOT READY, not an application-health diagnosis. Dedication/exclusivity is owner-reviewed, not a global lock.";
    public static IReadOnlySet<string> SupportedTables { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "AppRequests", "AppDependencies", "AppExceptions", "AppTraces", "AppEvents" };
    public static bool HasSupportedWorkspaceAuthorization(JsonElement workspace)
    {
        var features = Property(Property(workspace, "properties"), "features");
        return Property(features, "enableLogAccessUsingOnlyResourcePermissions").ValueKind == JsonValueKind.False
            && Property(features, "dataAuthorizationMode").ValueKind == JsonValueKind.False;
    }
    public static bool HasSupportedTableAuthorization(JsonElement table)
    {
        var properties = Property(table, "properties");
        var plan = Property(properties, "plan");
        var protection = Property(properties, "protectionLevel");
        return plan.ValueKind == JsonValueKind.String && plan.GetString() == "Analytics"
            && protection.ValueKind == JsonValueKind.String && protection.GetString() == "General";
    }
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    public static bool HasFreshEvidence(FactoryTelemetry authority, DateTimeOffset now) =>
        authority.State == TelemetryState.Ready && authority.Configuration.FreshnessSeconds > 0
        && authority.Proof is { State: TelemetryState.Ready, Reason: TelemetryReason.None } proof
        && proof.CheckId == authority.CheckId && proof.ConfigurationRevision == authority.ApprovalFingerprint
        && proof.SourcePrincipalId == authority.Configuration.SourceIdentity.PrincipalId
        && proof.ApplicationEnvironment == authority.Configuration.ApplicationEnvironment
        && proof.FactoryId == authority.Configuration.FactoryId && proof.OperationId == authority.OperationId
        && proof.WindowEnd <= now && authority.Configuration.Backends.All(b => proof.Records.Any(r =>
            r.BackendResourceId == b.ResourceId && r.WorkspaceId == b.WorkspaceId && b.Tables.Contains(r.Table)
            && b.ApplicationResourceIds.Contains(r.ApplicationResourceId, StringComparer.OrdinalIgnoreCase)
            && r.ObservedRecords > 0 && r.RecordTime >= now.AddSeconds(-authority.Configuration.FreshnessSeconds)
            && r.RecordTime <= now));
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, FactoryGitHubJson.Options))));
    public static bool IsProviderOperationId(string? value) =>
        value is { Length: >= 1 and <= FactoryTelemetryLimits.ProviderOperationIdCharacters } && value.All(c => char.IsAsciiHexDigit(c) || c == '-');
    public static string Query(string table) => SupportedTables.Contains(table)
        ? table + " | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type" : throw new JsonException("Unsupported Analytics table.");
    public static string StaleQuery(string table) => SupportedTables.Contains(table)
        ? table + " | top 1 by TimeGenerated desc | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type" : throw new JsonException("Unsupported Analytics table.");
    public static string HostTarget(FactoryTelemetryConfiguration configuration)
    {
        var prefix = "df-" + configuration.FactoryId.ToString("N")[..14];
        var suffix = "/providers/microsoft.app/managedenvironments/" + prefix + "-hosting";
        var hosting = configuration.HostingEnvironment.Target;
        if (!hosting.EndsWith(suffix, StringComparison.Ordinal)) throw new JsonException("Exact factory hosting binding required.");
        return hosting[..^suffix.Length] + "/providers/microsoft.app/containerapps/" + prefix + "-telemetry";
    }

    public static FactoryTelemetry ReadPreparation(FactoryManifest manifest, IReadOnlyDictionary<string, string> values, bool requireChecking = true)
    {
        var authority = JsonSerializer.Deserialize<FactoryTelemetry>(values[ProductConfigurationKeys.FactoryTelemetry], FactoryGitHubJson.Options)
            ?? throw new JsonException();
        var config = authority.Configuration;
        var source = config.SourceIdentity;
        if (authority.SchemaVersion != 1 || config.SchemaVersion != 1 || authority.OperationId == Guid.Empty
            || manifest.TelemetryOperationId != authority.OperationId || manifest.TelemetryApprovalFingerprint != authority.ApprovalFingerprint
            || Hash(config) != authority.ApprovalFingerprint || config.FactoryId != manifest.Identity.FactoryId
            || config.PlanFingerprint != manifest.Identity.PlanFingerprint || config.ApplicationEnvironment != manifest.Identity.ApplicationEnvironment
            || config.QueryRevision != QueryRevision || config.FreshnessSeconds is <= 0 or > 31536000
            || config.Queries.Count != config.Backends.SelectMany(b => b.Tables).Distinct(StringComparer.Ordinal).Count()
            || config.StaleQueries.Count != config.Queries.Count
            || config.Queries.Any(p => p.Value != Query(p.Key)) || config.StaleQueries.Any(p => p.Value != StaleQuery(p.Key))
            || config.ProductEndpoint != "https://dsfo-" + config.FactoryId.ToString("N") + ".azconfig.io"
            || source.FactoryId != config.FactoryId || source.LogicalIdentity != "factory:source-azuremonitor"
            || source.State != FoundationOperationState.Completed || source.Disposition != FoundationResourceDisposition.Created
            || source.ResourceId != source.Target || source.Incarnation is null || source.ProviderOperationId is null
            || source.OperationId != config.HostingEnvironment.OperationId
            || config.HostingEnvironment.FactoryId != config.FactoryId
            || config.HostingEnvironment.Disposition != FoundationResourceDisposition.Created
            || config.HostingEnvironment.State != FoundationOperationState.Completed
            || config.HostingEnvironment.ResourceId != config.HostingEnvironment.Target
            || config.HostingEnvironment.Incarnation is null || config.HostingEnvironment.ProviderOperationId is null
            || !Guid.TryParse(source.PrincipalId, out var principal) || principal == Guid.Empty
            || !Guid.TryParse(source.ClientId, out var client) || client == Guid.Empty
            || source.PrincipalId == manifest.Identity.AzureActorId
            || !source.Target.EndsWith("/df-" + config.FactoryId.ToString("N")[..14] + "-source", StringComparison.Ordinal)
            || !source.Target.StartsWith("/subscriptions/" + manifest.Identity.SubscriptionId + "/resourcegroups/", StringComparison.Ordinal)
            || Hash(config.AccessGrants) != Hash(Grants(config, manifest.Identity.SubscriptionId))
            || config.Backends.Count == 0 || config.Backends.Select(b => b.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Backends.Count
            || config.Backends.Any(b => !Guid.TryParse(b.WorkspaceId, out var id) || id == Guid.Empty
                || b.Tables.Count == 0 || b.Tables.Any(t => !SupportedTables.Contains(t))
                || b.ApplicationResourceIds.Count == 0 || b.ApplicationResourceIds.Any(id => !config.ApplicationResourceIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                    || !id.Contains("/providers/microsoft.insights/components/", StringComparison.Ordinal)))
            || requireChecking && (authority.State != TelemetryState.Partial || authority.Stage != "checking"
                || authority.CheckId is null || authority.CheckId == Guid.Empty))
            throw new JsonException("Current reviewed telemetry preparation required.");
        if (requireChecking) _ = ReadCompletedEffects(authority, values);
        return authority;
    }

    public static IReadOnlyList<FoundationOperation> ReadCompletedEffects(FactoryTelemetry authority, IReadOnlyDictionary<string, string> values)
    {
        var config = authority.Configuration;
        var host = HostTarget(config);
        var targets = config.AccessGrants.Select(g => g.Target).Concat([host, host + "/authconfigs/current"]).ToArray();
        if (authority.OperationCount != targets.Length || authority.OperationCount > 64 || authority.EffectHashes.Count != targets.Length)
            throw new JsonException();
        var effects = new List<FoundationOperation>();
        for (var i = 0; i < targets.Length; i++)
        {
            var key = ProductConfigurationKeys.TelemetryOperation(authority.OperationId, i);
            if (!values.TryGetValue(key, out var value) || Encoding.UTF8.GetByteCount(value) >= 10 * 1024) throw new JsonException();
            var effect = JsonSerializer.Deserialize<FoundationOperation>(value, FactoryGitHubJson.Options) ?? throw new JsonException();
            var hashKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (effect.SchemaVersion != 1 || effect.Kind != "decide-telemetry-operation"
                || effect.FactoryId != config.FactoryId || effect.OperationId != authority.OperationId || effect.Index != i
                || effect.Target != targets[i] || effect.ResourceId != targets[i]
                || effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                || effect.ClientRequestId == Guid.Empty || effect.StartedAt == default || effect.HttpStatus != 201
                || effect.RequestSha256.Length != 64 || !effect.RequestSha256.All(char.IsAsciiHexDigit)
                || !Guid.TryParse(effect.ProviderRequestId, out var providerId) || providerId == Guid.Empty
                || !Guid.TryParse(effect.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty
                || !DateTimeOffset.TryParse(effect.Incarnation, System.Globalization.CultureInfo.InvariantCulture, out _)
                || !authority.EffectHashes.TryGetValue(hashKey, out var hash) || hash != Hash(effect))
                throw new JsonException();
            if (i < config.AccessGrants.Count)
            {
                var grant = config.AccessGrants[i];
                if (effect.ApiVersion != "2022-04-01" || effect.RoleScope != grant.Scope || effect.RoleDefinitionId != grant.RoleDefinitionId
                    || effect.PrincipalId != grant.PrincipalId || effect.LogicalIdentity != "factory:source-azuremonitor"
                    || effect.Condition is not null || effect.ConditionVersion is not (null or "2.0")) throw new JsonException();
            }
            else if (effect.ApiVersion != "2025-01-01" || !IsProviderOperationId(effect.ProviderOperationId)
                || effect.RoleScope is not null || effect.PrincipalId is not null) throw new JsonException();
            effects.Add(effect);
        }
        return effects;
    }

    public static void RequireVerifiedHistory(FactoryTelemetry authority, IReadOnlyDictionary<string, string> values)
    {
        if (authority.State != TelemetryState.Ready || authority.LastFailure != TelemetryReason.None
            || authority.CheckId is null || authority.CheckId == Guid.Empty
            || authority.LastCheckReference != authority.VerifiedProofReference || authority.LastCheckSha256 != authority.VerifiedProofSha256
            || authority.VerifiedProofReference != ProductConfigurationKeys.TelemetryProof(authority.OperationId, authority.CheckId.Value)
            || !values.TryGetValue(authority.VerifiedProofReference, out var value))
            throw new JsonException("Complete telemetry check and verified receipt required.");
        var proof = JsonSerializer.Deserialize<TelemetryProof>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (Hash(proof) != authority.VerifiedProofSha256 || proof.State != TelemetryState.Ready || proof.Reason != TelemetryReason.None
            || proof.CheckId != authority.CheckId || proof.FactoryId != authority.Configuration.FactoryId || proof.OperationId != authority.OperationId
            || proof.ConfigurationRevision != authority.ApprovalFingerprint || proof.SourcePrincipalId != authority.Configuration.SourceIdentity.PrincipalId
            || proof.ApplicationEnvironment != authority.Configuration.ApplicationEnvironment
            || proof.WindowEnd > proof.ObservedAt || proof.WindowEnd - proof.WindowStart != TimeSpan.FromSeconds(authority.Configuration.FreshnessSeconds))
            throw new JsonException("Telemetry receipt identity or integrity mismatch.");
        FactoryTelemetryLimits.ValidateProof(authority.Configuration, proof);
        if (!HasFreshEvidence(authority with { Proof = proof }, proof.WindowEnd)
            || proof.Queries.Count != authority.Configuration.Backends.Sum(b => b.Tables.Count)
            || proof.Queries.Any(q => q.Reason is not (TelemetryReason.None or TelemetryReason.Empty or TelemetryReason.Stale)))
            throw new JsonException("Incomplete historical telemetry verification.");
    }

    public static IReadOnlyList<TelemetryAccessGrant> Grants(FactoryTelemetryConfiguration configuration, string subscription)
    {
        var source = configuration.SourceIdentity;
        var group = source.Target[..source.Target.IndexOf("/providers/", StringComparison.Ordinal)];
        return new[] { (Scope: group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + configuration.FactoryId.ToString("N"),
            Role: "516239f1-63e1-4d78-a4de-a74fb236a071") }
            .Concat(configuration.Backends.Select(b => (Scope: b.ResourceId, Role: "73c42c96-874c-492b-b04d-ab87d138a893")))
            .Select(pair =>
            {
                var name = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(source.PrincipalId + ":" + pair.Scope + ":" + pair.Role))[..16]);
                return new TelemetryAccessGrant(pair.Scope + "/providers/microsoft.authorization/roleassignments/" + name,
                    source.PrincipalId!, pair.Scope, "/subscriptions/" + subscription + "/providers/microsoft.authorization/roledefinitions/" + pair.Role);
            }).ToArray();
    }
}
