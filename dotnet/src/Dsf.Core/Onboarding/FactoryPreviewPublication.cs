using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum PreviewPublicationPhase { PublishingSeedCapability, SeedDispatched, SeedAwaitingApproval, PublishingExecution, ConfiguredAwaitingEvaluation, KnownFailed, Unknown }
public sealed record PreviewPublicationWrite(string Key, string Value, string? PreviousSha256);
public sealed record PreviewPublicationPolicy(string Revision, int RequestSeconds, int CleanupSeconds, int MaximumWrites,
    int ReviewBytes, int SeedBytes, int ResponseBytes, int MaximumRecords, int NamespaceBytes);
public sealed record PreviewPublicationReview(string Stage, string PreparationRevision, FactoryPreviewConfiguration Configuration,
    PreviewExecutionConfiguration? Execution, IReadOnlyList<PreviewPublicationWrite> Writes, string SeedEndpoint, PreviewCapabilityRequest SeedRequest)
{
    [JsonIgnore] public string ApprovalFingerprint => FactoryModelsValidation.Hash(this);
    public required PreviewSeedIntent SeedIntent { get; init; }
    public PreviewPublicationPolicy Policy { get; init; } = FactoryPreviewPublication.Policy;
}
public sealed record PreviewSeedIntent(string Endpoint, string Database, string Container, string Partition, string Document,
    PreviewExecutionPolicy Policy);
public sealed record PreviewPublicationJournal(int Protocol, Guid FactoryId, Guid OperationId, string PreparationRevision,
    string ApprovedBy, DateTimeOffset ApprovedAt, Guid Owner, PreviewPublicationPhase Phase, string ApprovalFingerprint,
    int WriteCount, PreviewExecutionSeed? Seed, string? Reason);
public sealed record PreviewPublicationStep(int Index, string Target, string PayloadSha256, Guid RequestId,
    PreviewPreparationEffectState State, string? NativeRevision);
public sealed record PreviewPublicationApproval(Guid OperationId, string Stage, string PreparationRevision,
    string ApprovalFingerprint, string ApprovedBy, DateTimeOffset ApprovedAt, Guid Owner);
public sealed record ConfirmedPreviewPreparation(PreviewPreparationPlan Plan, PreviewPreparationCoordinator Coordinator,
    IReadOnlyList<PreviewPreparationEffect> Effects, IReadOnlyList<PreviewPreparationReceipt> Receipts);

public static class FactoryPreviewPublication
{
    public const string Namespace = "dsf/factory/preview-publication/";
    public const string RootKey = Namespace + "current";
    public const string PreparationContentType = "application/vnd.dsf.preview-preparation+json;version=1";
    public static PreviewPublicationPolicy Policy { get; } =
        new("protected-preview-publication-v1", 3600, 30, 96, 512 * 1024, 16 * 1024, 4096, 1024, 4 * 1024 * 1024);
    public static string StepKey(Guid operation, string stage, int index) => Namespace + operation.ToString("D") + "/" + stage + "/" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    public static string ApprovalKey(Guid operation, string stage) => Namespace + operation.ToString("D") + "/" + stage + "/approval";
    public static Guid RequestId(Guid operation, string step) => new(Convert.FromHexString(FactoryModelsValidation.Hash(new { operation, step }))[..16]);
    public static bool IsPreparationRecord(string key) => key.StartsWith(FactoryPreviewPreparation.Namespace + "/", StringComparison.Ordinal);
    public static readonly JsonSerializerOptions Json = new(FactorySourceValidation.RecordJson) { IgnoreReadOnlyProperties = false };

    public static PreviewPublicationReview Review(ConfirmedPreviewPreparation preparation, FactoryModels models, FactoryGitHubAuthority github,
        PreviewExecutionSeed? seed, bool final)
    {
        var plan = preparation.Plan;
        var operation = plan.Selection.OperationId;
        var effects = Effects(preparation, plan.Manifest, models, github);
        var execution = seed is null ? null : new PreviewExecutionConfiguration(FactoryPreviewExecution.Policy, seed);
        var configuration = plan.Configuration with { PreparationRevision = plan.Fingerprint,
            ExecutionRevision = execution is null ? null : FactoryModelsValidation.Hash(execution), Execution = execution };
        var capability = new FactoryPreviewCapability(1, FactoryPreviewValidation.Kind, operation, FactoryModelsValidation.Hash(configuration),
            configuration, plan.Manifest.Identity.AzureActorId, preparation.Coordinator.ApprovedAt, PreviewCapabilityState.Prepared,
            effects.Select(FactoryModelsValidation.Hash).ToArray());
        var writes = new List<PreviewPublicationWrite>();
        if (!final)
        {
            foreach (var name in FactoryPreviewValidation.BindingNames) Add(ProductConfigurationKeys.PreviewBinding(operation, name), plan.Bindings[name]);
            foreach (var effect in effects) Add(ProductConfigurationKeys.PreviewOperation(operation, effect.Index), effect);
            if (plan.SupplementalConfiguration is { } optional) Add(FactoryPreviewSupplementalValidation.ConfigurationKey(operation), optional);
        }
        else Add(FactoryPreviewExecution.ConfigurationKey(operation), execution ?? throw new JsonException());
        var original = plan.Configuration with { PreparationRevision = plan.Fingerprint };
        var baseCapability = capability with { Configuration = original, ApprovalFingerprint = FactoryModelsValidation.Hash(original) };
        Add(ProductConfigurationKeys.FactoryPreview, capability, final ? Serialize(baseCapability) : null);
        Add(ProductConfigurationKeys.FactoryManifest, plan.Manifest with { PreviewOperationId = operation, PreviewApprovalFingerprint = capability.ApprovalFingerprint },
            Serialize(final ? plan.Manifest with { PreviewOperationId = operation, PreviewApprovalFingerprint = baseCapability.ApprovalFingerprint } : plan.Manifest));
        var endpoint = preparation.Effects[0].CouncilEndpoint ?? throw new JsonException();
        return new(final ? "execution" : "seed", plan.Fingerprint, configuration, execution, writes, endpoint,
            new(operation, capability.ApprovalFingerprint, RequestId(operation, "native-seed")))
        { SeedIntent = SeedIntent(plan, preparation.Coordinator.ApprovedAt) };

        void Add<T>(string key, T value, string? previous = null) =>
            writes.Add(new(key, Serialize(value), previous is null ? null : TextHash(Encoding.UTF8.GetBytes(previous))));
    }

    public static PreviewPublicationJournal ValidatePublished(ConfirmedPreviewPreparation preparation, FactoryModels models,
        FactoryGitHubAuthority github, IReadOnlyDictionary<string, string> values, DateTimeOffset now)
    {
        var journal = JsonSerializer.Deserialize<PreviewPublicationJournal>(values[RootKey], Json) ?? throw new JsonException();
        var seed = Review(preparation, models, github, null, false);
        var final = journal.Phase == PreviewPublicationPhase.ConfiguredAwaitingEvaluation;
        var execution = journal.Seed is null ? null : Review(preparation, models, github, journal.Seed, true);
        if (journal.Protocol != 1 || journal.FactoryId != preparation.Plan.Manifest.Identity.FactoryId
            || journal.OperationId != preparation.Plan.Selection.OperationId || journal.PreparationRevision != preparation.Plan.Fingerprint
            || journal.Owner == Guid.Empty || journal.ApprovedBy != preparation.Coordinator.ApprovedBy
            || journal.ApprovedAt < preparation.Coordinator.ApprovedAt || journal.ApprovedAt > now || journal.ApprovedAt.Offset != TimeSpan.Zero
            || journal.Phase is not (PreviewPublicationPhase.SeedDispatched or PreviewPublicationPhase.SeedAwaitingApproval or PreviewPublicationPhase.ConfiguredAwaitingEvaluation)
            || (journal.Phase == PreviewPublicationPhase.SeedDispatched ? journal.Seed is not null : journal.Seed is null))
            throw new JsonException("Complete acknowledged publication required.");
        var current = final ? execution ?? throw new JsonException() : seed;
        if (journal.ApprovalFingerprint != current.ApprovalFingerprint || journal.WriteCount != current.Writes.Count) throw new JsonException();
        var expected = new HashSet<string>(StringComparer.Ordinal) { RootKey };
        foreach (var stage in final ? new[] { seed, current } : new[] { seed })
        {
            var approvalKey = ApprovalKey(journal.OperationId, stage.Stage);
            expected.Add(approvalKey);
            var approval = JsonSerializer.Deserialize<PreviewPublicationApproval>(values[approvalKey], Json) ?? throw new JsonException();
            if (approval.OperationId != journal.OperationId || approval.Stage != stage.Stage || approval.PreparationRevision != preparation.Plan.Fingerprint
                || approval.ApprovalFingerprint != stage.ApprovalFingerprint || approval.ApprovedBy != journal.ApprovedBy
                || approval.ApprovedAt < preparation.Coordinator.ApprovedAt || approval.ApprovedAt > journal.ApprovedAt
                || approval.Owner == Guid.Empty || stage == current && (approval.Owner != journal.Owner || approval.ApprovedAt != journal.ApprovedAt))
                throw new JsonException("Original explicit publication approval required.");
            foreach (var (write, index) in stage.Writes.Select((w, i) => (w, i)))
            {
                var key = StepKey(journal.OperationId, stage.Stage, index);
                expected.Add(key);
                var step = JsonSerializer.Deserialize<PreviewPublicationStep>(values[key], Json) ?? throw new JsonException();
                if (step.Index != index || step.Target != write.Key || step.PayloadSha256 != TextHash(Encoding.UTF8.GetBytes(write.Value))
                    || step.RequestId != RequestId(journal.OperationId, stage.Stage + ":" + index)
                    || step.State != PreviewPreparationEffectState.Confirmed || string.IsNullOrEmpty(step.NativeRevision)) throw new JsonException();
                if (final && stage == seed && current.Writes.Any(w => w.Key == write.Key)) continue;
                if (values[write.Key] != write.Value) throw new JsonException("Protected published payload changed.");
            }
        }
        if (!expected.SetEquals(values.Keys.Where(k => k.StartsWith(Namespace, StringComparison.Ordinal)))) throw new JsonException();
        return journal;
    }

    private static PreviewSeedIntent SeedIntent(PreviewPreparationPlan plan, DateTimeOffset createdAt)
    {
        var initial = new
        {
            protocol = 1, id = FactoryPreviewExecution.Id(plan.Manifest.Identity.FactoryId, plan.Selection.OperationId),
            product = plan.Manifest.Identity.ProductKey, factoryId = plan.Manifest.Identity.FactoryId, operationId = plan.Selection.OperationId,
            createdAt, kind = "council-preview-evaluation", profile = "OnboardingPreview", checksum = "", owner = Guid.Empty,
            phase = "Prepared", inputs = (object?)null, run = (object?)null, pendingRun = (object?)null,
            dispatches = Array.Empty<object>(), failure = (object?)null, finalInputs = (object?)null, proof = (object?)null,
        };
        var document = JsonSerializer.SerializeToNode(initial, Json)!;
        document["checksum"] = FactoryModelsValidation.Hash(initial);
        return new("https://" + FactoryPreviewValidation.Prefix(plan.Manifest.Identity.FactoryId) + "-cosmos.documents.azure.com",
            plan.Manifest.Identity.ProductKey, FactoryPreviewValidation.PreviewContainer, plan.Manifest.Identity.ProductKey,
            document.ToJsonString(Json), FactoryPreviewExecution.Policy);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static ConfirmedPreviewPreparation ReadConfirmed(FactoryManifest manifest, IReadOnlyDictionary<string, string> values, DateTimeOffset now)
    {
        try
        {
            T Read<T>(string key) => JsonSerializer.Deserialize<T>(values[key], Json) ?? throw new JsonException();
            var root = Read<PreviewPreparationCoordinator>(FactoryPreviewPreparation.RootKey);
            var policy = FactoryPreviewPreparation.Policy;
            if (root.Protocol != 1 || root.Kind != FactoryPreviewPreparation.Kind || root.FactoryId != manifest.Identity.FactoryId
                || root.OperationId == Guid.Empty || root.State != PreviewPreparationState.ConfirmedNotReady || root.Owner != Guid.Empty
                || root.ApprovedBy != manifest.Identity.AzureActorId || root.ApprovedAt < manifest.Identity.CreatedAt || root.ApprovedAt > now
                || root.PlanBytes is < 1 || root.PlanBytes > policy.PlanBytes
                || root.PlanChunks != (root.PlanBytes + policy.ChunkBytes - 1) / policy.ChunkBytes || root.PlanChunks > policy.MaximumChunks)
                throw new JsonException();
            var keys = new HashSet<string>(StringComparer.Ordinal) { FactoryPreviewPreparation.RootKey };
            using var bytes = new MemoryStream(root.PlanBytes);
            for (var i = 0; i < root.PlanChunks; i++)
            {
                var key = FactoryPreviewPreparation.ChunkKey(root.OperationId, i);
                keys.Add(key);
                var chunk = Convert.FromBase64String(Read<string>(key));
                if (chunk.Length != Math.Min(policy.ChunkBytes, root.PlanBytes - i * policy.ChunkBytes)) throw new JsonException();
                bytes.Write(chunk);
            }
            if (bytes.Length != root.PlanBytes || TextHash(bytes.ToArray()) != root.PlanSha256) throw new JsonException();
            var plan = JsonSerializer.Deserialize<PreviewPreparationPlan>(bytes.ToArray(), Json) ?? throw new JsonException();
            FactoryPreviewPreparation.Validate(plan.Selection);
            if (plan.Protocol != 1 || plan.Policy != policy || plan.Selection.OperationId != root.OperationId
                || plan.Manifest.Identity != manifest.Identity || plan.Fingerprint != root.PlanFingerprint
                || plan.Steps.Count is < 1 || plan.Steps.Count > policy.MaximumEffects || root.ConfirmedEffects != plan.Steps.Count
                || FactoryPreviewValidation.ManifestBasis(manifest) != FactoryPreviewValidation.ManifestBasis(plan.Manifest))
                throw new JsonException();
            var effects = new List<PreviewPreparationEffect>();
            var receipts = new List<PreviewPreparationReceipt>();
            foreach (var step in plan.Steps)
            {
                var key = FactoryPreviewPreparation.EffectKey(root.OperationId, step.Index);
                keys.Add(key);
                var effect = Read<PreviewPreparationEffect>(key);
                if (step.Index != effects.Count || effect.Protocol != 1 || effect.FactoryId != root.FactoryId || effect.OperationId != root.OperationId
                    || effect.PlanFingerprint != plan.Fingerprint || effect.Index != step.Index || effect.Target != step.Target
                    || effect.BodySha256 != step.BodySha256 || effect.RequestId == Guid.Empty || effect.IntendedAt < root.ApprovedAt || effect.IntendedAt > now
                    || effect.State != PreviewPreparationEffectState.Confirmed || effect.ReceiptHashes.Count != step.ResourceTargets.Count
                    || effect.Acknowledgment is not { StatusCode: 201, BodySha256: not null, RequestId: not null, CorrelationId: not null } ack
                    || !FactorySourceValidation.Hash(ack.BodySha256)
                    || ack.ObservedAt < effect.IntendedAt || ack.ObservedAt > now || ack.ObservedAt.Offset != TimeSpan.Zero
                    || !Guid.TryParse(ack.RequestId, out var acknowledgedRequest) || acknowledgedRequest == Guid.Empty
                    || !Guid.TryParse(ack.CorrelationId, out var acknowledgedCorrelation) || acknowledgedCorrelation == Guid.Empty
                    || step.Action == PreviewPreparationAction.WorkloadDeployment && ack.ProvisioningState != "Succeeded")
                    throw new JsonException();
                for (var i = 0; i < step.ResourceTargets.Count; i++)
                {
                    key = FactoryPreviewPreparation.ReceiptKey(root.OperationId, step.Index, i);
                    keys.Add(key);
                    var receipt = Read<PreviewPreparationReceipt>(key);
                    if (receipt.Target != step.ResourceTargets[i] || FactoryModelsValidation.Hash(receipt) != effect.ReceiptHashes[i]
                        || !FactorySourceValidation.Hash(receipt.NativeRevision) || string.IsNullOrEmpty(receipt.Incarnation)
                        || receipt.StatusCode is not (200 or 201) || !Guid.TryParse(receipt.RequestId, out var request) || request == Guid.Empty
                        || !Guid.TryParse(receipt.CorrelationId, out var correlation) || correlation == Guid.Empty
                        || (step.Action == PreviewPreparationAction.WorkloadDeployment
                            ? receipt.DeploymentTarget != step.Target || !FactoryTelemetryValidation.IsProviderOperationId(receipt.OperationId)
                                || ack.DeploymentCorrelationId != receipt.CorrelationId
                            : receipt.ApiVersion != "2022-04-01" || receipt.DeploymentTarget is not null || receipt.OperationId is not null
                                || ack.RequestId != receipt.RequestId || ack.CorrelationId != receipt.CorrelationId))
                        throw new JsonException();
                    receipts.Add(receipt);
                }
                effects.Add(effect);
            }
            if (!keys.SetEquals(values.Keys.Where(IsPreparationRecord))
                || effects.Select(e => e.RequestId).Distinct().Count() != effects.Count) throw new JsonException();
            return new(plan, root, effects, receipts);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ConfirmedNativePreparationHistoryRequired"); }
    }

    public static FoundationOperation[] Effects(ConfirmedPreviewPreparation preparation, FactoryManifest manifest, FactoryModels models, FactoryGitHubAuthority github)
    {
        var plan = preparation.Plan;
        return FactoryPreviewValidation.Targets(manifest, models, github, plan.Selection.OperationId, plan.Configuration.MetadataRoleDefinitionId,
            plan.Configuration with { Supplemental = plan.SupplementalConfiguration }).Select((target, index) =>
        {
            var step = plan.Steps.Single(s => s.ResourceTargets.Contains(target.Target, StringComparer.Ordinal));
            var effect = preparation.Effects[step.Index];
            var receipt = preparation.Receipts.Single(r => r.Target == target.Target);
            if (receipt.ApiVersion != target.Api) throw new JsonException();
            return new FoundationOperation(1, FactoryPreviewValidation.OperationKind, manifest.Identity.FactoryId, plan.Selection.OperationId,
                index, target.Target, target.Api, effect.BodySha256, effect.RequestId, effect.IntendedAt,
                FoundationOperationState.Completed, FoundationResourceDisposition.Created)
            {
                ResourceId = target.Target, Incarnation = receipt.Incarnation, NativeRevision = receipt.NativeRevision,
                HttpStatus = receipt.StatusCode, ProviderRequestId = receipt.RequestId, ProviderCorrelationId = receipt.CorrelationId,
                ProviderOperationId = receipt.OperationId, DeploymentTarget = receipt.DeploymentTarget, Endpoint = receipt.Endpoint,
                PrincipalId = step.Grant?.PrincipalId, RoleDefinitionId = step.Grant?.RoleDefinitionId, RoleScope = step.Grant?.Scope,
            };
        }).ToArray();
    }

    private static string TextHash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
