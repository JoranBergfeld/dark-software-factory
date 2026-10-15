using System.Text.Json;

namespace Dsf.Core.Onboarding;

public sealed record FactoryLiveSelection(Guid OperationId, string RuntimeImage, string AudienceClientId, string MetadataRoleDefinitionId);
public sealed record FactoryLiveRequest(Guid OperationId, string RolloutFingerprint);
public sealed record FactoryLiveExecutionScope(Guid OperationId, string RolloutFingerprint, string AuthorityRevision);
public sealed record FactoryLiveObservation(int Protocol, Guid FactoryId, Guid InvocationId, string RolloutFingerprint,
    DateTimeOffset ObservedAt, PreviewReadiness Readiness, string? Reason, JsonElement? Evaluation)
{
    public string Mode => "NativeNonpublishingExercise";
    public bool IsActivated => false;
    public bool PublicationPermitted => false;
}
public enum FactoryLiveState { Reviewed, Preparing, PreparedAwaitingPublication, Blocked, Failed, Canceled, Unknown }
public sealed record FactoryLivePlan(int Protocol, FactoryLiveSelection Selection, FactoryActivationReview Activation,
    AuditRecordIdentity Approval, PreviewPreparationPlan Deployment)
{
    public string Fingerprint => FactoryModelsValidation.Hash(new { Protocol, Selection, Activation, Approval, Deployment });
}
public sealed record FactoryLiveCoordinator(int Protocol, Guid FactoryId, Guid OperationId, string Fingerprint, string PlanSha256,
    int PlanBytes, int PlanChunks, string ApprovedBy, DateTimeOffset ApprovedAt, FactoryLiveState State, int ConfirmedEffects);
public sealed record FactoryLiveCapability(FactoryLivePlan Plan, FactoryLiveCoordinator Coordinator,
    IReadOnlyList<PreviewPreparationEffect> Effects, IReadOnlyList<PreviewPreparationReceipt> Receipts)
{
    public string Endpoint => Effects.Single(e => e.Index == 0).CouncilEndpoint!;
    public string Revision => FactoryModelsValidation.Hash(new { Plan, Coordinator, Effects, Receipts });
}
public sealed record FactoryLiveResult(FactoryLiveState State, string Detail)
{
    public FactoryLivePlan? Plan { get; init; }
    public FactoryLiveCoordinator? Coordinator { get; init; }
    public IReadOnlyList<PreviewPreparationReceipt> Receipts { get; init; } = [];
    public string? ApprovalFingerprint => Plan?.Fingerprint;
    public bool IsActivated => false;
    public bool HistoricallyCompleted => false;
    public string PromotionBlockedBy => "Create-once publication capability and separate final promotion approval required.";
}

public static class FactoryLive
{
    public const string Prefix = FactoryAudit.Namespace + "live/";
    public const string RootKey = Prefix + "current";
    public const string Verb = "serve-decide";
    public const int MaximumRecords = 256;
    public static string ChunkKey(Guid operation, int index) => Prefix + $"{operation:N}/plan/{index:D4}";
    public static string EffectKey(Guid operation, int index) => Prefix + $"{operation:N}/effects/{index:D4}";
    public static string ReceiptKey(Guid operation, int effect, int index) => EffectKey(operation, effect) + $"/receipts/{index:D4}";
    public static string HostTarget(FactoryLivePlan plan) => HostTarget(plan.Deployment);
    public static string HostTarget(PreviewPreparationPlan plan)
    {
        var account = plan.Bindings["account"].Target;
        return account[..account.IndexOf("/providers/", StringComparison.Ordinal)] + "/providers/microsoft.app/containerapps/"
            + FactoryPreviewValidation.Prefix(plan.Manifest.Identity.FactoryId) + "-decide";
    }
    public static void Validate(FactoryLiveSelection selection)
    {
        if (selection.OperationId == Guid.Empty) throw new JsonException("Nonempty live rollout operation required.");
        FactorySourceValidation.ValidateSelection(new(selection.RuntimeImage, selection.AudienceClientId));
        var role = selection.MetadataRoleDefinitionId?.Split('/') ?? [];
        if (role.Length != 7 || role[1] != "subscriptions" || !Guid.TryParse(role[2], out var subscription) || subscription == Guid.Empty
            || role[3] != "providers" || role[4] != "microsoft.authorization" || role[5] != "roledefinitions"
            || !Guid.TryParse(role[6], out var id) || id == Guid.Empty) throw new JsonException("Exact native metadata role required.");
    }

    public static FactoryLivePlan ReadPlan(FactoryLiveCoordinator coordinator, IReadOnlyDictionary<string, string> values)
    {
        if (coordinator.Protocol != 1 || coordinator.FactoryId == Guid.Empty || coordinator.OperationId == Guid.Empty
            || !Enum.IsDefined(coordinator.State) || coordinator.PlanBytes is < 1 or > 524288
            || coordinator.PlanChunks != (coordinator.PlanBytes + 4095) / 4096 || coordinator.PlanChunks > 128
            || coordinator.ConfirmedEffects is < 0 or > 3) throw new JsonException();
        using var stream = new MemoryStream(coordinator.PlanBytes);
        for (var i = 0; i < coordinator.PlanChunks; i++)
        {
            var bytes = Convert.FromBase64String(JsonSerializer.Deserialize<string>(values[ChunkKey(coordinator.OperationId, i)],
                FactorySourceValidation.RecordJson) ?? throw new JsonException());
            if (bytes.Length != Math.Min(4096, coordinator.PlanBytes - i * 4096)) throw new JsonException();
            stream.Write(bytes);
        }
        if (stream.Length != coordinator.PlanBytes) throw new JsonException();
        var content = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        if (FactoryAudit.TextHash(content) != coordinator.PlanSha256) throw new JsonException();
        var plan = JsonSerializer.Deserialize<FactoryLivePlan>(content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        Validate(plan.Selection);
        if (plan.Protocol != 1 || plan.Selection.OperationId != coordinator.OperationId || plan.Fingerprint != coordinator.Fingerprint
            || plan.Deployment.Manifest.Identity.FactoryId != coordinator.FactoryId || plan.Activation.FactoryId != coordinator.FactoryId
            || plan.Deployment.Manifest.Identity.AzureActorId != coordinator.ApprovedBy || plan.Activation.ReviewedBy != coordinator.ApprovedBy
            || plan.Approval.FactoryId != coordinator.FactoryId || plan.Approval.Store != AuditStoreKind.Governance
            || plan.Approval.Key != FactoryActivation.ApprovalKey(plan.Activation.OperationId))
            throw new JsonException("Exact original live rollout and approval references required.");
        return plan;
    }

    public static FactoryLiveCapability ReadConfirmed(IReadOnlyDictionary<string, string> values, DateTimeOffset now)
    {
        var root = Parse<FactoryLiveCoordinator>(values[RootKey]);
        var plan = ReadPlan(root, values);
        if (root.State != FactoryLiveState.PreparedAwaitingPublication || root.ConfirmedEffects != 3
            || root.ApprovedAt < plan.Deployment.Manifest.Identity.CreatedAt || root.ApprovedAt > now)
            throw new JsonException("Complete original operator-reviewed native rollout required.");
        var expected = new HashSet<string> { RootKey };
        for (var i = 0; i < root.PlanChunks; i++) expected.Add(ChunkKey(root.OperationId, i));
        var effects = new List<PreviewPreparationEffect>();
        var receipts = new List<PreviewPreparationReceipt>();
        foreach (var step in plan.Deployment.Steps)
        {
            var key = EffectKey(root.OperationId, step.Index);
            var effect = Parse<PreviewPreparationEffect>(values[key]);
            if (effect.Protocol != 1 || effect.FactoryId != root.FactoryId || effect.OperationId != root.OperationId
                || effect.PlanFingerprint != plan.Fingerprint || effect.Index != step.Index || effect.Target != step.Target
                || effect.BodySha256 != step.BodySha256 || effect.State != PreviewPreparationEffectState.Confirmed
                || effect.ReceiptHashes.Count != step.ResourceTargets.Count || effect.RequestId == Guid.Empty
                || effect.IntendedAt < root.ApprovedAt || effect.IntendedAt > now) throw new JsonException();
            effects.Add(effect);
            expected.Add(key);
            for (var i = 0; i < effect.ReceiptHashes.Count; i++)
            {
                key = ReceiptKey(root.OperationId, step.Index, i);
                var receipt = Parse<PreviewPreparationReceipt>(values[key]);
                if (FactoryModelsValidation.Hash(receipt) != effect.ReceiptHashes[i] || !step.ResourceTargets.Contains(receipt.Target)
                    || receipt.StatusCode is not (200 or 201)
                    || step.Action == PreviewPreparationAction.MetadataAssignment && receipt.StatusCode != 201
                    || !Guid.TryParse(receipt.RequestId, out var request) || request == Guid.Empty)
                    throw new JsonException();
                expected.Add(key);
                receipts.Add(receipt);
            }
        }
        if (!expected.SetEquals(values.Keys) || receipts.Select(r => r.Target).Distinct().Count() != receipts.Count)
            throw new JsonException("Original rollout namespace is incomplete or expanded.");
        var host = HostTarget(plan);
        if (plan.Deployment.Steps is not [{ Index: 0, Action: PreviewPreparationAction.WorkloadDeployment } workload,
            { Index: 1, Action: PreviewPreparationAction.MetadataAssignment, Grant: { } },
            { Index: 2, Action: PreviewPreparationAction.MetadataAssignment, Grant: { } }]
            || !workload.ResourceTargets.SequenceEqual([host, host + "/authconfigs/current"])
            || workload.BodySha256 != FactoryAudit.TextHash(plan.Deployment.WorkloadBody)
            || workload.Target != host[..host.IndexOf("/providers/", StringComparison.Ordinal)]
                + "/providers/microsoft.resources/deployments/live-" + root.OperationId.ToString("N"))
            throw new JsonException("Exact live host and scoped read-only grant required.");
        foreach (var assignment in plan.Deployment.Steps.Skip(1))
        {
            var grant = assignment.Grant!;
            if (assignment.Target != grant.Target || !assignment.ResourceTargets.SequenceEqual([grant.Target])
                || grant.Scope != (assignment.Index == 1 ? host : workload.Target)
                || grant.PrincipalId != plan.Deployment.Bindings["council"].PrincipalId
                || grant.RoleDefinitionId != plan.Selection.MetadataRoleDefinitionId
                || !grant.Actions.SequenceEqual(FactoryPreviewValidation.MetadataActions)
                || grant.Target != FactoryModelsValidation.Grant(grant.Scope, grant.RoleDefinitionId, grant.PrincipalId, grant.RoleRevision).Target
                || assignment.BodySha256 != FactoryAudit.TextHash(FactoryPreviewPreparation.GrantBody(grant)))
                throw new JsonException("Exact scoped host/deployment metadata grants required.");
        }
        _ = FactoryGovernance.NormalizeEndpoint(effects[0].CouncilEndpoint!);
        return new(plan, root, effects, receipts);
    }

    public static void ValidateCreation(FactoryLiveCapability live, IReadOnlyDictionary<string, JsonElement> resources,
        JsonElement deployment, JsonElement operationSet)
    {
        var effect = live.Effects[0];
        var properties = deployment.GetProperty("properties");
        if (properties.GetProperty("provisioningState").GetString() != "Succeeded"
            || effect.Acknowledgment is not { StatusCode: 201 } acknowledgment
            || properties.GetProperty("correlationId").GetString() != acknowledgment.DeploymentCorrelationId
            || effect.DeploymentOutputsSha256 != FactoryModelsValidation.Hash(properties.GetProperty("outputs"))
            || operationSet.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException("Original native live deployment acknowledgement required.");
        var operations = operationSet.GetProperty("value").EnumerateArray().ToArray();
        var created = operations.Where(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString() == "Create").ToArray();
        if (operations.Length > 128 || operations.Any(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString()
                is not ("Create" or "Read" or "EvaluateDeploymentOutput"))
            || !created.Select(o => o.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString()!)
                .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(live.Plan.Deployment.Steps[0].ResourceTargets.Order(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase))
            throw new JsonException("Original complete native creation operation set required.");
        foreach (var receipt in live.Receipts)
        {
            var resource = resources[receipt.Target];
            if (receipt.DeploymentTarget is not null)
            {
                var observed = FactoryHostedWorkloadPolicy.ObserveExactHost(resource, receipt.Target);
                var operation = operations.Single(o => o.GetProperty("operationId").GetString() == receipt.OperationId).GetProperty("properties");
                if (observed.Incarnation != receipt.Incarnation || observed.Revision != receipt.NativeRevision
                    || receipt.DeploymentTarget != live.Plan.Deployment.Steps[0].Target
                    || receipt.CorrelationId != properties.GetProperty("correlationId").GetString()
                    || !Guid.TryParse(operation.GetProperty("trackingId").GetString(), out var tracking) || tracking.ToString() != receipt.RequestId
                    || !string.Equals(operation.GetProperty("targetResource").GetProperty("id").GetString(), receipt.Target, StringComparison.OrdinalIgnoreCase)
                    || operation.GetProperty("provisioningOperation").GetString() != "Create"
                    || operation.GetProperty("provisioningState").GetString() != "Succeeded"
                    || (operation.GetProperty("statusCode").GetString() switch { "Created" or "201" => 201, "OK" or "200" => 200, _ => 0 }) != receipt.StatusCode)
                    throw new JsonException("Current native creation evidence differs from original receipt.");
            }
            else
            {
                var grant = live.Plan.Deployment.Steps.Single(s => s.Grant?.Target == receipt.Target).Grant!;
                var p = resource.GetProperty("properties");
                var createdAt = p.GetProperty("createdOn").GetDateTimeOffset();
                if (FactoryModelsValidation.Hash(p) != receipt.NativeRevision || p.GetProperty("principalId").GetString() != grant.PrincipalId
                    || p.GetProperty("principalType").GetString() != "ServicePrincipal"
                    || !string.Equals(p.GetProperty("scope").GetString(), grant.Scope, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(p.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
                    || p.GetProperty("createdBy").GetString() != live.Coordinator.ApprovedBy
                    || createdAt != DateTimeOffset.Parse(receipt.Incarnation, System.Globalization.CultureInfo.InvariantCulture)
                    || p.TryGetProperty("updatedOn", out var updated) && updated.ValueKind != JsonValueKind.Null && updated.GetDateTimeOffset() != createdAt
                    || p.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
                    || p.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0")
                    throw new JsonException("Original exact native scoped read grant required.");
            }
        }
    }

    public static string ValidateHost(FactoryLivePlan plan, IReadOnlyDictionary<string, JsonElement> resources)
    {
        var host = HostTarget(plan);
        var identity = plan.Deployment.Manifest.Identity;
        var council = plan.Deployment.Bindings["council"];
        var endpoint = FactoryHostedWorkloadPolicy.Validate(resources[host], resources[host + "/authconfigs/current"], council,
            plan.Deployment.Bindings["hosting"], plan.Selection.RuntimeImage, Verb, identity.ProductKey,
            $"https://dsfo-{identity.FactoryId:N}.azconfig.io", plan.Deployment.Manifest.ProfilePin, identity.TenantId,
            plan.Selection.AudienceClientId, [identity.AzureActorId, council.PrincipalId!]);
        var template = resources[host].GetProperty("properties").GetProperty("template");
        var authorization = resources[host + "/authconfigs/current"].GetProperty("properties").GetProperty("identityProviders")
            .GetProperty("azureActiveDirectory").GetProperty("validation").GetProperty("defaultAuthorizationPolicy");
        if (authorization.TryGetProperty("allowedApplications", out var applications) && applications.GetArrayLength() != 0) throw new JsonException();
        var capacity = template.GetProperty("containers")[0].GetProperty("resources");
        if (capacity.GetProperty("cpu").GetDouble() != 0.25 || capacity.GetProperty("memory").GetString() != "0.5Gi"
            || template.GetProperty("scale").GetProperty("minReplicas").GetInt32() != 1
            || template.GetProperty("scale").GetProperty("maxReplicas").GetInt32() != 1) throw new JsonException();
        return endpoint;
    }

    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
}
