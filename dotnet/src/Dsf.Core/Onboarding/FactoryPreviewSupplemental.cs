using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record PreviewSupplementalHost(string Kind, string IdentityRevision, string PrincipalId, string ClientId, string RuntimeImage, string AudienceClientId,
    string MetadataRoleDefinitionId, string MetadataRoleRevision, string MetadataScope);
public sealed record PreviewSupplementalConfiguration(string ConfigurationRevision, string DeploymentRevision, IReadOnlyList<PreviewSupplementalHost> Hosts);
public sealed record PreviewSupplementalCoordinator(string Endpoint, string AudienceClientId, string PrincipalId, string ClientId);
public sealed record PreviewSupplementalRequest(PreviewCapabilityRequest Preview, string Kind);
public sealed record PreviewSupplementalAuthority(PreviewSupplementalRequest Request, string GovernanceRevision, string CapabilityRevision,
    PreviewSupplementalHost Host, FoundationOperation Identity, SupplementalHostConfiguration Workload, FoundationOperation Hosting, FoundationOperation? Vault,
    IReadOnlyDictionary<string, string> PrerequisiteRevisions, IReadOnlyList<SupplementalGrant> Grants,
    IReadOnlyList<FoundationOperation> Effects, FoundationOperation MetadataGrant);
public sealed record PreviewSupplementalSourceEvidence(PreviewSupplementalAuthority Authority, SupplementalEvidence Evidence,
    IReadOnlyDictionary<string, string> NativeRevisions);
public sealed record PreviewSupplementalEvidence(Guid RequestId, string GovernanceRevision, SupplementalState State,
    IReadOnlyList<PreviewSupplementalSourceEvidence> Sources);

public static class FactoryPreviewSupplementalValidation
{
    public const string Namespace = "dsf/factory/supplemental";
    public const string ConfigurationPrefix = "dsf/factory/preview-supplemental/";
    public static IReadOnlyList<string> NamespacePrefixes { get; } = [Namespace, ConfigurationPrefix];
    public static bool IsSelectionRecord(string key) => NamespacePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
    public static string ConfigurationKey(Guid operation) => ConfigurationPrefix + operation.ToString("D");
    public const int SourceBytes = 256 * 1024;
    public const int ResponseBytes = 1024 * 1024;
    public static PreviewHostKind HostKind(string kind) => kind switch
    {
        "foundryiq" => PreviewHostKind.FoundryEvidence, "webiq" => PreviewHostKind.WebEvidence,
        _ => throw new JsonException("Exact selected supplemental kind required."),
    };
    public static string MetadataScope(SupplementalConfiguration config, SupplementalSourceSelection source) =>
        source.Kind == "webiq" ? config.Vault!.Target : source.SearchResourceId!;
    public static IReadOnlyList<string> MetadataActions(string kind) => kind == "webiq"
        ? ["Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read", "Microsoft.KeyVault/vaults/read"]
        : kind == "foundryiq" ? ["Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read"] : throw new JsonException();

    public static FactorySupplemental? Read(FactoryManifest manifest, FactoryPreviewConfiguration preview, IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(ProductConfigurationKeys.FactorySupplemental, out var raw))
        {
            if (manifest.SupplementalOperationId is not null || manifest.SupplementalApprovalFingerprint is not null
                || values.Keys.Any(IsSelectionRecord)
                || preview.SupplementalRevision is not null)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "SupplementalNamespaceNotPristine");
            return null;
        }
        var selected = FactorySupplementalValidation.Read(manifest, raw);
        if (selected.State != SupplementalState.ReadySupplemental)
            throw new PreviewCapabilityException(selected.State switch { SupplementalState.Canceled => FactoryCharterState.Canceled,
                SupplementalState.Unknown => FactoryCharterState.Unknown, SupplementalState.Partial => FactoryCharterState.Partial,
                SupplementalState.Failed => FactoryCharterState.Failed, _ => FactoryCharterState.Blocked }, "CurrentSupplementalPreparationRequired");
        var effects = ReadEffects(selected, values);
        var config = preview.Supplemental ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewSupplementalConsumersRequired");
        if (config.Hosts.Any(h => h is null) || config.ConfigurationRevision != selected.ApprovalFingerprint || config.DeploymentRevision != FactoryModelsValidation.Hash(effects)
            || !config.Hosts.Select(h => h.Kind).SequenceEqual(selected.Configuration!.Selection.Sources.Select(s => s.Kind))
            || config.Hosts.Select(h => h.PrincipalId).Distinct().Count() != config.Hosts.Count)
            throw new JsonException("Complete exact selected supplemental preview configuration required.");
        foreach (var host in config.Hosts)
        {
            var identity = effects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(selected.Configuration!, host.Kind));
            if (FactoryModelsValidation.Hash(identity) != host.IdentityRevision || identity.PrincipalId != host.PrincipalId || identity.ClientId != host.ClientId
                || host.MetadataScope != MetadataScope(selected.Configuration!, selected.Configuration!.Selection.Sources.Single(s => s.Kind == host.Kind))
                || !FactorySourceValidation.Hash(host.MetadataRoleRevision)
                || !host.MetadataRoleDefinitionId.StartsWith("/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/", StringComparison.Ordinal)
                || !Guid.TryParseExact(host.MetadataRoleDefinitionId.Split('/').Last(), "D", out var role) || role == Guid.Empty)
                throw new JsonException("Exact supplemental identity and metadata role required.");
            FactorySourceValidation.ValidateSelection(new(host.RuntimeImage, host.AudienceClientId));
            var check = selected.Checks.Single(c => c.Kind == host.Kind);
            if (check.ProofKey != ProductConfigurationKeys.SupplementalProof(selected.OperationId, selected.CheckId!.Value, host.Kind))
                throw new JsonException();
            var proof = FactorySourceValidation.ReadRecord<SupplementalProof>(values, check.ProofKey);
            if (FactoryTelemetryValidation.Hash(proof) != check.ProofSha256 || proof.Kind != check.Kind || proof.State != check.State || proof.Reason != check.Reason)
                throw new JsonException("Supplemental receipt integrity required.");
            FactorySupplementalValidation.ValidateProof(selected, identity, proof, proof.ObservedAt);
        }
        return selected;
    }

    public static IReadOnlyList<FoundationOperation> ReadEffects(FactorySupplemental selected, IReadOnlyDictionary<string, string> values)
    {
        var targets = FactorySupplementalValidation.Targets(selected.Configuration!);
        var resources = FactorySupplementalValidation.Resources(selected.Configuration!);
        if (selected.OperationCount != targets.Count || selected.EffectHashes.Count != targets.Count) throw new JsonException();
        return targets.Select((target, i) =>
        {
            var effect = FactorySourceValidation.ReadRecord<FoundationOperation>(values, ProductConfigurationKeys.SupplementalOperation(selected.OperationId, i));
            var resource = resources.Single(r => r.IdentityTarget == target.Target || r.HostTarget == target.Target
                || r.HostTarget + "/authconfigs/current" == target.Target || r.Grants.Any(g => g.Target == target.Target));
            var hosted = target.Target == resource.HostTarget || target.Target == resource.HostTarget + "/authconfigs/current";
            if (effect.SchemaVersion != 1 || effect.Kind != FactorySupplementalValidation.OperationKind || effect.FactoryId != selected.Configuration!.FactoryId
                || effect.OperationId != selected.OperationId || effect.Index != i || effect.Target != target.Target || effect.ApiVersion != target.Api
                || effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                || effect.ResourceId != effect.Target || !DateTimeOffset.TryParse(effect.Incarnation, System.Globalization.CultureInfo.InvariantCulture, out _)
                || !FactorySourceValidation.Hash(effect.NativeRevision) || !FactorySourceValidation.Hash(effect.RequestSha256)
                || effect.HttpStatus != 201 || effect.ClientRequestId == Guid.Empty || effect.StartedAt == default
                || effect.LogicalIdentity != "factory:supplemental-" + resource.Kind
                || (hosted ? !FactoryTelemetryValidation.IsProviderOperationId(effect.ProviderOperationId)
                    || effect.DeploymentTarget != selected.Configuration.FactoryGroup + "/providers/microsoft.resources/deployments/sp-"
                        + selected.OperationId.ToString("N") + "-" + resource.Kind
                    : effect.ProviderOperationId is not null || effect.DeploymentTarget is not null)
                || !Guid.TryParse(effect.ProviderRequestId, out var request) || request == Guid.Empty
                || !Guid.TryParse(effect.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty
                || FactoryTelemetryValidation.Hash(effect) != selected.EffectHashes[i]) throw new JsonException("Complete supplemental deployment receipts required.");
            return effect;
        }).ToArray();
    }

    public static void ValidateEvidence(PreviewSupplementalAuthority authority, SupplementalEvidence evidence, DateTimeOffset now)
    {
        var proof = evidence.Proof;
        var config = authority.Workload;
        FactorySupplementalValidation.ValidateProof(config, new(authority.Request.Preview.RequestId, config.ConfigurationRevision), proof, now);
        if (evidence.Items.Count > 16 || evidence.Items.Any(i => i is null)
            || proof.Records.Count != evidence.Items.Count || evidence.Items.Select(i => i.Reference).Distinct().Count() != evidence.Items.Count)
            throw new JsonException("Unconfirmed supplemental evidence envelope.");
        if (proof.State != SupplementalState.ReadySupplemental)
        {
            if (proof.State == SupplementalState.NotSelected || evidence.Items.Count != 0 || proof.Reason is SupplementalReason.None or SupplementalReason.Empty)
                throw new JsonException();
            return;
        }
        if (!FactorySourceValidation.Hash(proof.NativeSourceRevision) || proof.Reason != (evidence.Items.Count == 0 ? SupplementalReason.Empty : SupplementalReason.None))
            throw new JsonException();
        for (var i = 0; i < evidence.Items.Count; i++)
        {
            var item = evidence.Items[i];
            if (string.IsNullOrWhiteSpace(item.Content) || item.Content.Length > 8192 || item.Reference.Length > 320 || item.Reference.Any(char.IsControl)
                || proof.Records[i].Reference != item.Reference || proof.Records[i].ContentSha256 != FactoryTelemetryValidation.Hash(item.Content)
                || !(config.Source.Kind == "webiq" ? item.Reference.StartsWith("webiq:sha256:", StringComparison.Ordinal)
                        && FactorySourceValidation.Hash(item.Reference["webiq:sha256:".Length..])
                    : config.Source.KnowledgeSources.Any(s => item.Reference.StartsWith("foundryiq:" + FactorySupplementalValidation.SearchEndpoint(config.Source)
                        + "#" + config.Source.KnowledgeBase + "/" + s.Name + "/", StringComparison.Ordinal)
                        && FactorySourceValidation.Hash(item.Reference.Split('/').Last()))))
                throw new JsonException("Unbound supplemental content or reference.");
        }
    }
}
