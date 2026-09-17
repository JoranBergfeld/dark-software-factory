using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record PreviewTelemetrySelection(
    string Purpose, string ConfigurationRevision, string DeploymentRevision, FoundationOperation SourceIdentity,
    IReadOnlyList<string> Applications, string RuntimeImage, string AudienceClientId,
    string ProjectionRevision, int RowsPerTable, IReadOnlyDictionary<string, string> Queries,
    string MetadataRoleDefinitionId, string MetadataRoleRevision, IReadOnlyDictionary<string, string> AccessRoleRevisions);

public sealed record PreviewTelemetryRow(TelemetryRecordReference Reference, string QuerySha256, string ContentSha256,
    IReadOnlyDictionary<string, JsonElement> Fields);

public sealed record TelemetryEvidenceCapture(TelemetryProof Proof, IReadOnlyList<PreviewTelemetryRow> Rows,
    IReadOnlyDictionary<string, string> NativeRevisions);

public sealed record PreviewTelemetryEvidence(Guid RequestId, string GovernanceRevision, string SourceAuthorityRevision,
    string SelectionRevision, TelemetryEvidenceCapture Capture);

public static class FactoryPreviewTelemetryValidation
{
    public const string Purpose = "council-telemetry-evidence-v1";
    public const string Projection = "azuremonitor-app-content-v1";
    public const string Coverage = "Explicit latest-row sample per selected table, NOT exhaustive telemetry. "
        + FactoryTelemetryValidation.Coverage;
    public const int MaximumRows = 32;
    public const int ResponseBytes = 128 * 1024;
    public const int FieldBytes = 2048;
    public static IReadOnlyList<string> MetadataActions { get; } =
    [
        "Microsoft.App/containerApps/read", "Microsoft.App/containerApps/authConfigs/read", "Microsoft.App/managedEnvironments/read",
        "Microsoft.AppConfiguration/configurationStores/read", "Microsoft.ManagedIdentity/userAssignedIdentities/read",
        "Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read",
        "Microsoft.Resources/deployments/operations/read", "Microsoft.Insights/components/read",
    ];

    public static IReadOnlyList<(string Name, string Type)> Fields(string table) => table switch
    {
        "AppRequests" => [("Name", "string"), ("Success", "bool"), ("ResultCode", "string"), ("DurationMs", "real")],
        "AppDependencies" => [("Name", "string"), ("DependencyType", "string"), ("Success", "bool"), ("ResultCode", "string"), ("DurationMs", "real")],
        "AppExceptions" => [("ExceptionType", "string"), ("OuterMessage", "string")],
        "AppTraces" => [("Message", "string"), ("SeverityLevel", "int")],
        "AppEvents" => [("Name", "string")],
        _ => throw new JsonException("Unsupported telemetry content projection."),
    };

    public static string Query(string table, int rows) => table + " | top " + rows.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " by TimeGenerated desc | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type, "
        + string.Join(", ", Fields(table).Select(f => f.Name));

    public static string OriginalHost(string group, Guid factory) =>
        group + "/providers/microsoft.app/containerapps/" + FactoryPreviewValidation.Prefix(factory) + "-telemetry";

    public static IReadOnlyList<string> MetadataScopes(FactoryManifest manifest, FactoryModels models, Guid operation, PreviewTelemetrySelection selection)
    {
        var group = FactoryPreviewValidation.Group(models);
        return new[] { selection.SourceIdentity.Target, models.Bindings![0].Target, models.Bindings[2].Target,
            group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + manifest.Identity.FactoryId.ToString("N"),
            OriginalHost(group, manifest.Identity.FactoryId),
            FactoryPreviewValidation.HostTarget(group, manifest.Identity.FactoryId, PreviewHostKind.TelemetryEvidence),
            FactoryPreviewValidation.Deployment(group, operation) }.Concat(selection.Applications).ToArray();
    }

    public static FactoryTelemetry Read(FactoryManifest manifest, FactoryPreviewConfiguration preview, IReadOnlyDictionary<string, string> values)
    {
        var selection = preview.Telemetry ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked,
            "PreviewTelemetryProjectionRequired:dsf/factory/preview.configuration.telemetry");
        var authority = FactoryTelemetryValidation.ReadPreparation(manifest, values, requireChecking: false);
        if (authority.State is TelemetryState.Unknown or TelemetryState.Canceled or TelemetryState.Failed or TelemetryState.Partial)
            throw new PreviewCapabilityException(authority.State switch { TelemetryState.Canceled => FactoryCharterState.Canceled,
                TelemetryState.Failed => FactoryCharterState.Failed, TelemetryState.Partial => FactoryCharterState.Partial, _ => FactoryCharterState.Unknown },
                "CurrentTelemetryPreparationUnconfirmed");
        var effects = FactoryTelemetryValidation.ReadCompletedEffects(authority, values);
        var config = authority.Configuration;
        var applications = config.Backends.SelectMany(b => b.ApplicationResourceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var rolePrefix = "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/";
        if (selection.Purpose != Purpose || selection.ProjectionRevision != Projection
            || selection.ConfigurationRevision != authority.ApprovalFingerprint || selection.DeploymentRevision != FactoryModelsValidation.Hash(effects)
            || authority.State != TelemetryState.Ready || authority.Stage != "observed"
            || FactoryModelsValidation.Hash(selection.SourceIdentity) != FactoryModelsValidation.Hash(config.SourceIdentity)
            || !selection.Applications.SequenceEqual(applications) || applications.Length is < 1 or > 8
            || selection.RowsPerTable is < 1 or > 8 || config.Backends.Sum(b => b.Tables.Count) * selection.RowsPerTable > MaximumRows
            || !FactorySourceValidation.Hash(selection.MetadataRoleRevision)
            || !selection.MetadataRoleDefinitionId.StartsWith(rolePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(selection.MetadataRoleDefinitionId[rolePrefix.Length..], "D", out var role) || role == Guid.Empty
            || selection.Queries.Count != config.Queries.Count || config.Queries.Keys.Any(table =>
                !selection.Queries.TryGetValue(table, out var query) || query != Query(table, selection.RowsPerTable))
            || selection.AccessRoleRevisions.Count != config.AccessGrants.Select(g => g.RoleDefinitionId).Distinct().Count()
            || config.AccessGrants.Any(g => !selection.AccessRoleRevisions.TryGetValue(g.RoleDefinitionId, out var revision) || !FactorySourceValidation.Hash(revision)))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedTelemetryProjectionOrPreparationChanged");
        FactorySourceValidation.ValidateSelection(new(selection.RuntimeImage, selection.AudienceClientId));
        FactoryTelemetryValidation.RequireVerifiedHistory(authority, values);
        return authority;
    }

    public static void ValidateFields(string table, IReadOnlyDictionary<string, JsonElement> values)
    {
        var fields = Fields(table);
        if (values.Count != fields.Count) throw new JsonException("Exact projected fields required.");
        foreach (var (name, type) in fields)
        {
            if (!values.TryGetValue(name, out var value)) throw new JsonException("Required content field missing.");
            var valid = type switch
            {
                "string" => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                    && FactoryTelemetryLimits.StringJsonBytes(value.GetString()!) <= FieldBytes
                    && !value.GetString()!.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')),
                "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "real" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0,
                "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number is >= 0 and <= 4,
                _ => false,
            };
            if (!valid) throw new JsonException("Unsupported, absent or oversized projected application content.");
        }
    }

    public static void ValidateCapture(FactoryTelemetry authority, PreviewTelemetrySelection selection, Guid request,
        TelemetryEvidenceCapture capture, DateTimeOffset now)
    {
        var config = authority.Configuration;
        var proof = capture.Proof;
        FactoryTelemetryLimits.ValidateProof(config, proof, selection);
        if (!Enum.IsDefined(proof.State) || !Enum.IsDefined(proof.Reason) || proof.FactoryId != config.FactoryId
            || proof.OperationId != authority.OperationId || proof.ConfigurationRevision != authority.ApprovalFingerprint
            || proof.SourcePrincipalId != config.SourceIdentity.PrincipalId || proof.ApplicationEnvironment != config.ApplicationEnvironment
            || proof.CheckId != request || proof.ObservedAt < now.AddMinutes(-3) || proof.ObservedAt > now
            || proof.WindowEnd > proof.ObservedAt || proof.WindowEnd < now.AddMinutes(-3)
            || proof.WindowEnd - proof.WindowStart != TimeSpan.FromSeconds(config.FreshnessSeconds)
            || capture.Rows.Count > MaximumRows || capture.NativeRevisions.Count > 128
            || capture.NativeRevisions.Any(p => p.Key.Length > 2048 || !FactorySourceValidation.Hash(p.Value)))
            throw new JsonException("Unconfirmed telemetry evidence envelope.");
        if (proof.State != TelemetryState.Ready)
        {
            if (capture.Rows.Count != 0 || proof.Reason == TelemetryReason.None) throw new JsonException();
            return;
        }
        if (proof.Reason != TelemetryReason.None || capture.Rows.Count == 0 || capture.NativeRevisions.Count == 0
            || proof.Queries.Count != config.Backends.Sum(b => b.Tables.Count)
            || proof.Queries.Sum(q => q.Records) != capture.Rows.Count
            || proof.Records.Count != proof.Queries.Count(q => q.Reason == TelemetryReason.None)
            || !capture.NativeRevisions.Keys.Order(StringComparer.Ordinal).SequenceEqual(NativeTargets(config).Order(StringComparer.Ordinal))
            || capture.Rows.Select(r => (r.Reference.BackendResourceId, r.Reference.Table, r.Reference.ItemId)).Distinct().Count() != capture.Rows.Count
            || config.Backends.Any(b => !capture.Rows.Any(r => r.Reference.BackendResourceId == b.ResourceId)))
            throw new JsonException("Complete current application evidence required.");
        foreach (var query in proof.Queries)
        {
            var rows = capture.Rows.Where(r => r.Reference.BackendResourceId == query.BackendResourceId && r.Reference.Table == query.Table).ToArray();
            if (query.Reason is not (TelemetryReason.None or TelemetryReason.Empty or TelemetryReason.Stale)
                || rows.Length != query.Records || rows.Length > selection.RowsPerTable
                || query.Reason == TelemetryReason.None != (rows.Length > 0)
                || rows.Length > 0 && (query.LatestRecord is null
                    || !rows.Any(r => FactoryModelsValidation.Hash(r.Reference) == FactoryModelsValidation.Hash(query.LatestRecord))
                    || proof.Records.Count(r => FactoryModelsValidation.Hash(r) == FactoryModelsValidation.Hash(query.LatestRecord)) != 1
                    || rows.Any(r => r.Reference.RecordTime > query.LatestRecord.RecordTime))
                || rows.Length == 0 && (query.Reason == TelemetryReason.Empty && query.LatestRecord is not null
                    || query.Reason == TelemetryReason.Stale && (query.LatestRecord is null || query.LatestRecord.RecordTime >= proof.WindowStart)))
                throw new JsonException("Partial content query observation.");
            foreach (var row in rows)
            {
                var backend = config.Backends.Single(b => b.ResourceId == row.Reference.BackendResourceId);
                if (row.Reference.WorkspaceId != backend.WorkspaceId || !backend.ApplicationResourceIds.Contains(row.Reference.ApplicationResourceId)
                    || row.Reference.RecordTime < now.AddSeconds(-config.FreshnessSeconds) || row.Reference.RecordTime > proof.WindowEnd
                    || row.Reference.ObservedRecords != rows.Length || string.IsNullOrWhiteSpace(row.Reference.ItemId)
                    || row.Reference.ItemId.Length > FactoryTelemetryLimits.ItemIdCharacters || row.Reference.ItemId.Any(char.IsControl)
                    || row.QuerySha256 != query.QuerySha256 || row.ContentSha256 != FactoryModelsValidation.Hash(row.Fields))
                    throw new JsonException("Stale or unbound application content.");
                ValidateFields(row.Reference.Table, row.Fields);
            }
        }
    }

    public static IReadOnlyList<string> NativeTargets(FactoryTelemetryConfiguration config) => config.Backends.SelectMany(backend =>
    {
        var grant = config.AccessGrants.Single(g => g.Scope == backend.ResourceId);
        return new[] { backend.ResourceId, grant.Target,
            backend.ResourceId + "/providers/microsoft.authorization/roledefinitions/" + grant.RoleDefinitionId.Split('/').Last(),
            backend.ResourceId + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + grant.PrincipalId + "')" }
            .Concat(backend.Tables.Select(table => backend.ResourceId + "/tables/" + table)).Concat(backend.ApplicationResourceIds);
    }).Distinct(StringComparer.Ordinal).ToArray();

    public static void RequireGrant(JsonElement native, TelemetryAccessGrant grant, FoundationOperation receipt, string actor)
    {
        var p = native.GetProperty("properties");
        if (!Same(native.GetProperty("id").GetString(), grant.Target) || !Same(p.GetProperty("scope").GetString(), grant.Scope)
            || p.GetProperty("principalId").GetString() != grant.PrincipalId || p.GetProperty("principalType").GetString() != "ServicePrincipal"
            || !Same(p.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId) || p.GetProperty("createdBy").GetString() != actor
            || p.GetProperty("createdOn").GetDateTimeOffset() != DateTimeOffset.Parse(receipt.Incarnation!, System.Globalization.CultureInfo.InvariantCulture)
            || p.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
            || p.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0")
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentTelemetryGrantChanged");
    }

    public static void RequireRole(JsonElement native, string id, string revision)
    {
        var p = native.GetProperty("properties");
        if (!Same(native.GetProperty("id").GetString(), id) || p.GetProperty("type").GetString() != "BuiltInRole"
            || FactoryModelsValidation.Hash(p) != revision || p.GetProperty("permissions").GetArrayLength() == 0) throw new JsonException();
        foreach (var permission in p.GetProperty("permissions").EnumerateArray())
        {
            var actions = FactoryModelsValidation.Strings(permission, "actions");
            var notActions = FactoryModelsValidation.Strings(permission, "notActions");
            var data = FactoryModelsValidation.Strings(permission, "dataActions");
            if (FactoryModelsValidation.Strings(permission, "notDataActions").Length != 0) throw new JsonException();
            if (id.EndsWith("/516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.Ordinal))
            {
                if (actions.Length != 0 || notActions.Length != 0 || data.Length == 0
                    || data.Any(a => !a.StartsWith("Microsoft.AppConfiguration/configurationStores/", StringComparison.OrdinalIgnoreCase)
                        || !a.EndsWith("/read", StringComparison.OrdinalIgnoreCase))) throw new JsonException();
            }
            else
            {
                string[] permitted = ["*/read", "Microsoft.OperationalInsights/workspaces/analytics/query/action",
                    "Microsoft.OperationalInsights/workspaces/search/action", "Microsoft.Support/*"];
                if (!actions.Contains("*/read", StringComparer.OrdinalIgnoreCase)
                    || actions.Any(a => !permitted.Contains(a, StringComparer.OrdinalIgnoreCase))
                    || !notActions.Contains("Microsoft.OperationalInsights/workspaces/sharedKeys/read", StringComparer.OrdinalIgnoreCase)
                    || data.Any(a => !a.Equals("Microsoft.OperationalInsights/workspaces/jobs/export/action", StringComparison.OrdinalIgnoreCase)))
                    throw new JsonException();
            }
        }
    }

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
