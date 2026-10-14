using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum PreviewCapabilityState { Prepared, Partial, Blocked, Canceled, Unknown }
public enum PreviewHostKind { Council, SourceContext, ModelEvaluation, TelemetryEvidence, FoundryEvidence, WebEvidence }

public sealed record PreviewCapabilityRequest(Guid OperationId, string ApprovalFingerprint, Guid RequestId);

public sealed record FactoryPreviewConfiguration(
    int Protocol, string Purpose, Guid FactoryId, string ProfilePin, string PlanFingerprint, string ManifestBasisRevision,
    string SourceConfigurationRevision, string SourceDeploymentRevision, string ModelConfigurationRevision, string ModelDeploymentRevision,
    Guid AdoptedCharterRevision, string RuntimeImage, string AudienceClientId,
    string MetadataRoleDefinitionId, string MetadataRoleRevision, IReadOnlyDictionary<string, string> BindingHashes)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreviewModelHostConfiguration? ModelEvaluation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreviewTelemetrySelection? Telemetry { get; init; }
    [JsonIgnore]
    public PreviewSupplementalConfiguration? Supplemental { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupplementalRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreviewInputConfiguration? Inputs { get; init; }
    [JsonIgnore]
    public PreviewExecutionConfiguration? Execution { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreparationRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PreparationOperationId { get; init; }
}

public sealed record FactoryPreviewCapability(
    int Protocol, string Kind, Guid OperationId, string ApprovalFingerprint, FactoryPreviewConfiguration Configuration,
    string ApprovedBy, DateTimeOffset ApprovedAt, PreviewCapabilityState State, IReadOnlyList<string> EffectHashes);

public sealed record PreviewCapabilitySnapshot(
    FactoryManifest Manifest, FactoryPreviewCapability Capability, FoundationOperation Council, FoundationOperation Code,
    string? CouncilEndpoint, string? SourceContextEndpoint, string Revision)
{
    public string GovernanceRevision { get; init; } = "";
    public string? ModelEndpoint { get; init; }
    public FactoryModels? Models { get; init; }
    public string? TelemetryEndpoint { get; init; }
    public FactoryTelemetry? Telemetry { get; init; }
    public IReadOnlyList<FoundationOperation> TelemetryEffects { get; init; } = [];
    public FactorySupplemental? Supplemental { get; init; }
    public IReadOnlyList<FoundationOperation> SupplementalEffects { get; init; } = [];
    public IReadOnlyList<FoundationOperation> Effects { get; init; } = [];
    public IReadOnlyDictionary<string, string> SupplementalEndpoints { get; init; } = new Dictionary<string, string>();
    public PreviewPublicationJournal? Publication { get; init; }
    public PreviewSeedIntent? SeedIntent { get; init; }
}

public sealed record PreviewSourceContext(
    Guid RequestId, Guid OperationId, string ApprovalFingerprint, string CapabilityRevision, SourceCharterContextResult Context);

public sealed record PreviewContextFailure(FactoryCharterState State, string Reason);

/// <summary>Current protected records and native bindings; no readiness or publication permission.</summary>
public interface IFactoryPreviewCapability
{
    Task<PreviewCapabilitySnapshot> CheckAsync(PreviewCapabilityRequest request, CancellationToken cancellationToken);
}

public sealed class PreviewCapabilityException(FactoryCharterState state, string reason) : Exception(reason)
{
    public FactoryCharterState State { get; } = state;
}

public static class FactoryPreviewValidation
{
    public const string Purpose = "supervised-proposal-preview-v1";
    public const string Kind = "decide-preview-capability";
    public const string OperationKind = "decide-preview-operation";
    public const string PreviewContainer = "council-preview";
    public const string RecordContentType = "application/vnd.dsf.preview+json;version=1";
    public const int ResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(180);
    public static IReadOnlyList<string> BindingNames { get; } = ["council", "code", "hosting", "account", "database", "runs"];
    public static IReadOnlyList<string> MetadataActions { get; } =
    [
        "Microsoft.App/containerApps/read", "Microsoft.App/containerApps/authConfigs/read", "Microsoft.App/managedEnvironments/read",
        "Microsoft.AppConfiguration/configurationStores/read",
        "Microsoft.ManagedIdentity/userAssignedIdentities/read", "Microsoft.DocumentDB/databaseAccounts/read",
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/read", "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/read",
        "Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions/read", "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments/read",
        "Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read",
        "Microsoft.Resources/deployments/read", "Microsoft.Resources/deployments/operations/read",
    ];
    public static IReadOnlyList<string> StateDataActions { get; } =
    [
        "Microsoft.DocumentDB/databaseAccounts/readMetadata",
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read",
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create",
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace",
    ];

    public static string ManifestBasis(FactoryManifest manifest) =>
        FactoryModelsValidation.Hash(manifest with { PreviewOperationId = null, PreviewApprovalFingerprint = null });
    public static string Group(FactoryModels models) => models.Bindings![2].Target[..models.Bindings[2].Target.IndexOf("/providers/", StringComparison.Ordinal)];
    public static string Prefix(Guid factory) => "df-" + factory.ToString("N")[..14];
    public static string HostTarget(string group, Guid factory, PreviewHostKind kind) =>
        group + "/providers/microsoft.app/containerapps/" + Prefix(factory) + (kind switch
        {
            PreviewHostKind.Council => "-preview",
            PreviewHostKind.SourceContext => "-context",
            PreviewHostKind.ModelEvaluation => "-preview-models",
            PreviewHostKind.TelemetryEvidence => "-preview-telemetry",
            PreviewHostKind.FoundryEvidence => "-preview-foundryiq",
            PreviewHostKind.WebEvidence => "-preview-webiq",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    public static IReadOnlyList<PreviewHostKind> HostKinds(FactoryPreviewConfiguration? config) =>
        new[] { PreviewHostKind.Council, PreviewHostKind.SourceContext }
            .Concat(config?.ModelEvaluation is null ? [] : new[] { PreviewHostKind.ModelEvaluation })
            .Concat(config?.Telemetry is null ? [] : new[] { PreviewHostKind.TelemetryEvidence })
            .Concat(config?.Supplemental?.Hosts.Select(h => FactoryPreviewSupplementalValidation.HostKind(h.Kind)) ?? []).ToArray();
    public static string Deployment(string group, Guid operation) => group + "/providers/microsoft.resources/deployments/preview-" + operation.ToString("N");
    public static string Account(string group, Guid factory) => group + "/providers/microsoft.documentdb/databaseaccounts/" + Prefix(factory) + "-cosmos";
    public static string Database(string account, string product) => account + "/sqldatabases/" + product;
    public static string Container(string account, string product, string container) => Database(account, product) + "/containers/" + container;
    public static string SqlScope(string account, string product, string container) => account + "/dbs/" + product + "/colls/" + container;
    public static string SqlRole(string account, Guid factory) => account + "/sqlroledefinitions/" + StableId(factory + ":preview-state");
    public static string SqlAssignment(string account, string principal, string scope, string role) =>
        account + "/sqlroleassignments/" + StableId(principal + ":" + scope + ":" + role);
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);

    public static IReadOnlyList<string> MetadataScopes(FactoryManifest manifest, FactoryModels models, FactoryGitHubAuthority github, Guid operation,
        FactoryPreviewConfiguration? configuration = null)
    {
        var group = Group(models);
        return new[] { models.Bindings![0].Target, models.Bindings[1].Target, models.Bindings[2].Target,
            Account(group, manifest.Identity.FactoryId), FactorySourceValidation.HostTarget(github), FactoryModelsValidation.HostTarget(models),
            group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + manifest.Identity.FactoryId.ToString("N"),
            HostTarget(group, manifest.Identity.FactoryId, PreviewHostKind.Council),
            HostTarget(group, manifest.Identity.FactoryId, PreviewHostKind.SourceContext), Deployment(group, operation) }
            .Concat(configuration?.ModelEvaluation is null ? [] : new[] { HostTarget(group, manifest.Identity.FactoryId, PreviewHostKind.ModelEvaluation) })
            .Concat(configuration?.Telemetry is not { } telemetry ? [] : new[] { telemetry.SourceIdentity.Target,
                FactoryPreviewTelemetryValidation.OriginalHost(group, manifest.Identity.FactoryId),
                HostTarget(group, manifest.Identity.FactoryId, PreviewHostKind.TelemetryEvidence) })
            .Concat(configuration?.Supplemental?.Hosts.SelectMany(h => new[] {
                group + "/providers/microsoft.managedidentity/userassignedidentities/" + Prefix(manifest.Identity.FactoryId) + "-" + h.Kind,
                group + "/providers/microsoft.app/containerapps/" + Prefix(manifest.Identity.FactoryId) + "-" + h.Kind,
                HostTarget(group, manifest.Identity.FactoryId, FactoryPreviewSupplementalValidation.HostKind(h.Kind)) }) ?? []).ToArray();
    }

    public static IReadOnlyList<(string Target, string Api)> Targets(
        FactoryManifest manifest, FactoryModels models, FactoryGitHubAuthority github, Guid operation, string metadataRole,
        FactoryPreviewConfiguration? configuration = null)
    {
        var group = Group(models);
        var account = Account(group, manifest.Identity.FactoryId);
        var role = SqlRole(account, manifest.Identity.FactoryId);
        var preparationOperation = configuration?.PreparationOperationId ?? operation;
        return new[] { (Container(account, manifest.Identity.ProductKey, PreviewContainer), "2024-11-15"), (role, "2024-11-15") }
            .Concat(new[] { "runs", PreviewContainer }.Select(name => (SqlAssignment(account, models.Bindings![0].PrincipalId!,
                SqlScope(account, manifest.Identity.ProductKey, name), role), "2024-11-15")))
            .Concat(MetadataScopes(manifest, models, github, preparationOperation, configuration).Select(scope =>
                (FactoryModelsValidation.Grant(scope, metadataRole, models.Bindings![0].PrincipalId!, "").Target, "2022-04-01")))
            .Concat(HostKinds(configuration).SelectMany(kind =>
            {
                var host = HostTarget(group, manifest.Identity.FactoryId, kind);
                return new[] { (host, "2025-01-01"), (host + "/authconfigs/current", "2025-01-01") };
            })).Concat(configuration?.Telemetry is not { } telemetry ? [] :
                FactoryPreviewTelemetryValidation.MetadataScopes(manifest, models, preparationOperation, telemetry).Select(scope =>
                    (FactoryModelsValidation.Grant(scope, telemetry.MetadataRoleDefinitionId, telemetry.SourceIdentity.PrincipalId!, "").Target, "2022-04-01")))
            .Concat(configuration?.Supplemental?.Hosts.Select(h =>
                (FactoryModelsValidation.Grant(h.MetadataScope, h.MetadataRoleDefinitionId, h.PrincipalId, "").Target, "2022-04-01")) ?? []).ToArray();
    }

    public static (FactoryPreviewCapability Capability, Dictionary<string, FoundationOperation> Bindings, FoundationOperation[] Effects)
        Read(FactoryManifest manifest, FactoryModels models, FactoryGitHubAuthority github, FactorySource source,
            IReadOnlyList<FoundationOperation> sourceEffects, IReadOnlyDictionary<string, string> values,
            PreviewCapabilityRequest request, DateTimeOffset now)
    {
        if (request.OperationId == Guid.Empty || request.RequestId == Guid.Empty || !FactorySourceValidation.Hash(request.ApprovalFingerprint))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "InvalidPreviewCapabilityRequest");
        if (!values.ContainsKey(ProductConfigurationKeys.FactoryPreview))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ProtectedPreviewCapabilityMissing");
        var capability = FactorySourceValidation.ReadRecord<FactoryPreviewCapability>(values, ProductConfigurationKeys.FactoryPreview);
        var config = capability.Configuration;
        var executionKey = FactoryPreviewExecution.ConfigurationKey(capability.OperationId);
        if (config.ExecutionRevision is { } executionRevision)
        {
            var protectedExecution = FactorySourceValidation.ReadRecord<PreviewExecutionConfiguration>(values, executionKey);
            if (!FactorySourceValidation.Hash(executionRevision) || FactoryModelsValidation.Hash(protectedExecution) != executionRevision)
                throw new JsonException("Exact protected preview execution configuration required.");
            config = config with { Execution = protectedExecution };
            capability = capability with { Configuration = config };
        }
        else if (values.ContainsKey(executionKey)) throw new JsonException("Unpinned preview execution configuration.");
        if (config.SupplementalRevision is { } supplementalRevision)
        {
            var supplemental = FactorySourceValidation.ReadRecord<PreviewSupplementalConfiguration>(values,
                FactoryPreviewSupplementalValidation.ConfigurationKey(capability.OperationId));
            if (!FactorySourceValidation.Hash(supplementalRevision) || FactoryModelsValidation.Hash(supplemental) != supplementalRevision)
                throw new JsonException("Exact protected preview supplemental configuration required.");
            config = config with { Supplemental = supplemental };
            capability = capability with { Configuration = config };
        }
        if (capability.Protocol != 1 || capability.Kind != Kind || config.Protocol != 1 || config.Purpose != Purpose
            || capability.OperationId != request.OperationId || capability.ApprovalFingerprint != request.ApprovalFingerprint
            || manifest.PreviewOperationId != capability.OperationId || manifest.PreviewApprovalFingerprint != capability.ApprovalFingerprint
            || capability.ApprovalFingerprint != FactoryModelsValidation.Hash(config) || capability.State != PreviewCapabilityState.Prepared
            || capability.ApprovedBy != manifest.Identity.AzureActorId || capability.ApprovedAt < manifest.Identity.CreatedAt
            || capability.ApprovedAt > now || capability.ApprovedAt.Offset != TimeSpan.Zero
            || config.FactoryId != manifest.Identity.FactoryId || config.ProfilePin != manifest.ProfilePin
            || config.PlanFingerprint != manifest.Identity.PlanFingerprint || config.ManifestBasisRevision != ManifestBasis(manifest)
            || config.SourceConfigurationRevision != source.ApprovalFingerprint
            || config.SourceDeploymentRevision != FactoryModelsValidation.Hash(sourceEffects)
            || config.ModelConfigurationRevision != models.ApprovalFingerprint
            || config.ModelDeploymentRevision != FactoryModelsValidation.Hash(models.Effects)
            || config.AdoptedCharterRevision == Guid.Empty || config.BindingHashes.Count != BindingNames.Count
            || BindingNames.Any(name => !config.BindingHashes.TryGetValue(name, out var hash) || !FactorySourceValidation.Hash(hash))
            || !FactorySourceValidation.Hash(config.MetadataRoleRevision))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewApprovalOrCurrentInputsChanged");
        FactorySourceValidation.ValidateSelection(new(config.RuntimeImage, config.AudienceClientId));
        if (config.Inputs is not null) FactoryPreviewInputValidation.Validate(config.Inputs);
        if (config.Execution is { } execution && (execution.Policy != FactoryPreviewExecution.Policy
            || config.Inputs is null || execution.Seed.Id != FactoryPreviewExecution.Id(manifest.Identity.FactoryId, capability.OperationId)
            || !FactorySourceValidation.Hash(execution.Seed.Checksum) || execution.Seed.ResourceId.Length is < 1 or > 512
            || execution.Seed.ETag.Length is < 3 or > 256 || execution.Seed.ETag[0] != '"' || execution.Seed.ETag[^1] != '"'))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewExecutionPolicyAndSeedRequired");
        if (config.Execution is not null) _ = NativeCosmosResourceId.RequireDocument(config.Execution.Seed.ResourceId);
        if (config.ModelEvaluation is { } evaluation)
        {
            if (evaluation.Purpose != PreviewModelHostConfiguration.PurposeName) throw new JsonException();
            FactorySourceValidation.ValidateSelection(new(evaluation.RuntimeImage, evaluation.AudienceClientId));
        }
        if (config.Telemetry is not null) _ = FactoryPreviewTelemetryValidation.Read(manifest, config, values);
        _ = FactoryPreviewSupplementalValidation.Read(manifest, config, values);
        var rolePrefix = "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/";
        if (!config.MetadataRoleDefinitionId.StartsWith(rolePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(config.MetadataRoleDefinitionId[rolePrefix.Length..], "D", out var roleId) || roleId == Guid.Empty)
            throw new JsonException();
        var bindings = BindingNames.ToDictionary(name => name, name =>
            FactorySourceValidation.ReadRecord<FoundationOperation>(values, ProductConfigurationKeys.PreviewBinding(capability.OperationId, name)));
        var group = Group(models);
        var account = Account(group, config.FactoryId);
        string[] expected = [models.Bindings![0].Target, models.Bindings[1].Target, models.Bindings[2].Target,
            account, Database(account, manifest.Identity.ProductKey), Container(account, manifest.Identity.ProductKey, "runs")];
        for (var i = 0; i < BindingNames.Count; i++)
        {
            var binding = bindings[BindingNames[i]];
            if (binding.SchemaVersion != 1 || FactoryModelsValidation.Hash(binding) != config.BindingHashes[BindingNames[i]]
                || binding.Target != expected[i] || binding.ResourceId != binding.Target || binding.FactoryId != config.FactoryId
                || binding.OperationId != models.Bindings[0].OperationId || binding.Kind != "decide-foundation-operation"
                || binding.State != FoundationOperationState.Completed || binding.Disposition != FoundationResourceDisposition.Created
                || binding.StartedAt < manifest.Identity.CreatedAt || binding.StartedAt > now || binding.ClientRequestId == Guid.Empty
                || !FactorySourceValidation.Hash(binding.RequestSha256) || binding.HttpStatus is not (200 or 201)
                || string.IsNullOrWhiteSpace(binding.Incarnation) || !FactoryTelemetryValidation.IsProviderOperationId(binding.ProviderOperationId)
                || !Guid.TryParse(binding.ProviderRequestId, out var provider) || provider == Guid.Empty
                || !Guid.TryParse(binding.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty
                || i >= 3 && binding.ApiVersion != "2024-11-15"
                || i < 3 && FactoryModelsValidation.Hash(binding) != FactoryModelsValidation.Hash(models.Bindings[i]))
                throw new JsonException("Original factory binding required.");
        }
        var targets = Targets(manifest, models, github, capability.OperationId, config.MetadataRoleDefinitionId, config);
        FoundationOperation[]? prepared = null;
        if (config.PreparationRevision is not null)
        {
            var history = FactoryPreviewPublication.ReadConfirmed(manifest, values, now);
            if (history.Plan.Fingerprint != config.PreparationRevision) throw new JsonException("Original native preparation required.");
            prepared = FactoryPreviewPublication.Effects(history, manifest, models, github);
            var publication = FactoryPreviewPublication.ValidatePublished(history, models, github, values, now);
            if (publication.Protocol != 1 || publication.FactoryId != manifest.Identity.FactoryId || publication.OperationId != capability.OperationId
                || publication.PreparationRevision != config.PreparationRevision || publication.ApprovedBy != manifest.Identity.AzureActorId
                || publication.Owner == Guid.Empty || publication.ApprovedAt > now
                || publication.Phase is not (PreviewPublicationPhase.SeedDispatched or PreviewPublicationPhase.SeedAwaitingApproval or PreviewPublicationPhase.ConfiguredAwaitingEvaluation)
                || publication.Phase == PreviewPublicationPhase.SeedDispatched
                    && request.RequestId != FactoryPreviewPublication.RequestId(capability.OperationId, "native-seed")
                || publication.Phase == PreviewPublicationPhase.ConfiguredAwaitingEvaluation && config.Execution?.Seed != publication.Seed
                || publication.Phase != PreviewPublicationPhase.ConfiguredAwaitingEvaluation && config.Execution is not null)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewPublicationIncompleteOrQuarantined");
        }
        else if (values.Keys.Any(k => FactoryPreviewPublication.IsPreparationRecord(k) || k.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal)))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreparedPreviewHistoryCannotBeDowngraded");
        if (capability.EffectHashes.Count != targets.Count) throw new JsonException("Complete preview deployment receipts required.");
        var effects = targets.Select((target, i) =>
        {
            var effect = FactorySourceValidation.ReadRecord<FoundationOperation>(values, ProductConfigurationKeys.PreviewOperation(capability.OperationId, i));
            if (effect.SchemaVersion != 1 || effect.Kind != OperationKind || effect.FactoryId != config.FactoryId
                || effect.OperationId != capability.OperationId || effect.Index != i || effect.Target != target.Target
                || effect.ResourceId != effect.Target || effect.ApiVersion != target.Api
                || (prepared is null ? effect.DeploymentTarget != Deployment(group, capability.OperationId)
                    || !FactoryTelemetryValidation.IsProviderOperationId(effect.ProviderOperationId)
                    : FactoryModelsValidation.Hash(effect) != FactoryModelsValidation.Hash(prepared[i]))
                || effect.StartedAt < capability.ApprovedAt || effect.StartedAt > now || effect.ClientRequestId == Guid.Empty
                || effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                || effect.HttpStatus is not (200 or 201) || !FactorySourceValidation.Hash(effect.RequestSha256)
                || !FactorySourceValidation.Hash(effect.NativeRevision)
                || !Guid.TryParse(effect.ProviderRequestId, out var provider) || provider == Guid.Empty
                || !Guid.TryParse(effect.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty
                || FactoryModelsValidation.Hash(effect) != capability.EffectHashes[i])
                throw new JsonException("Unconfirmed preview deployment effect.");
            return effect;
        }).ToArray();
        return (capability, bindings, effects);
    }
}
