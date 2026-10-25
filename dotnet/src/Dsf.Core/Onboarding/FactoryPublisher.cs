using System.Text;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryPublisherState { Reviewed, Preparing, PublicationPreparedAwaitingPromotion, Blocked, Failed, Canceled, Unknown }
public sealed record FactoryPublisherPlan(int Protocol, FactoryLiveSelection Selection, FactoryActivationReview Activation,
    AuditRecordIdentity Approval, AuditRecordIdentity Live, AuditRecordIdentity GitHub,
    FactoryGitHubAuthority GitHubAuthority, RepositoryMutationReview Automation, PreviewPreparationPlan Deployment)
{
    public string Fingerprint => FactoryModelsValidation.Hash(new { Protocol, Selection, Activation, Approval, Live, GitHub,
        GitHubAuthority, Automation, Deployment });
}
public sealed record FactoryPublisherCoordinator(int Protocol, Guid FactoryId, Guid OperationId, string Fingerprint,
    string PlanSha256, int PlanBytes, int PlanChunks, string ApprovedBy, DateTimeOffset ApprovedAt,
    FactoryPublisherState State, int ConfirmedEffects)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AuditRecordIdentity? NativeProviderProof { get; init; }
}
public sealed record FactoryPublisherCapability(FactoryPublisherPlan Plan, FactoryPublisherCoordinator Coordinator,
    IReadOnlyList<PreviewPreparationEffect> Effects, IReadOnlyList<PreviewPreparationReceipt> Receipts)
{
    public string ContextEndpoint => Effects[0].CouncilEndpoint!;
    public string PublisherEndpoint => Receipts.Single(r => r.Target == FactoryPublisher.HostTarget(Plan.Deployment, false)).Endpoint!;
    public string Revision => FactoryModelsValidation.Hash(new { Plan, Coordinator, Effects, Receipts });
}
public sealed record FactoryPublisherResult(FactoryPublisherState State, string Detail)
{
    public FactoryPublisherPlan? Plan { get; init; }
    public FactoryPublisherCoordinator? Coordinator { get; init; }
    public IReadOnlyList<PreviewPreparationReceipt> Receipts { get; init; } = [];
    public FactoryPublisherObservation? Observation { get; init; }
    public string? ApprovalFingerprint => Plan?.Fingerprint;
    public bool IsActivated => false;
    public bool HistoricallyCompleted => false;
}

public static class FactoryPublisher
{
    public const string Prefix = FactoryAudit.Namespace + "publisher/";
    public const string RootKey = Prefix + "current";
    public const string Verb = "serve-publisher";
    public const string ContextVerb = "serve-publication-context";
    public const int MaximumRecords = 512;
    public static string ChunkKey(Guid operation, int index) => Prefix + $"{operation:N}/plan/{index:D4}";
    public static string EffectKey(Guid operation, int index) => Prefix + $"{operation:N}/effects/{index:D4}";
    public static string ReceiptKey(Guid operation, int effect, int index) => EffectKey(operation, effect) + $"/receipts/{index:D4}";
    public static string ObservationKey(Guid operation) => Prefix + $"{operation:N}/native-capability-proof";
    public static string Group(PreviewPreparationPlan plan) => plan.Bindings["account"].Target.Split("/providers/", StringSplitOptions.None)[0];
    public static string HostTarget(PreviewPreparationPlan plan, bool context) => Group(plan) + "/providers/microsoft.app/containerapps/"
        + FactoryPreviewValidation.Prefix(plan.Manifest.Identity.FactoryId) + (context ? "-pub-context" : "-publisher");
    public static string Role(PreviewPreparationPlan plan, bool writer) => plan.Bindings["account"].Target + "/sqlroledefinitions/"
        + new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{plan.Manifest.Identity.FactoryId:D}:proposal-publications:{(writer ? "auth-write" : "council-read")}"))[..16]).ToString();
    public static string[] ReadDataActions => ["Microsoft.DocumentDB/databaseAccounts/readMetadata",
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read"];
    public static string[] WriteDataActions => [.. ReadDataActions,
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create",
        "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace"];
    public static string[] ResourceTargets(PreviewPreparationPlan plan)
    {
        var account = plan.Bindings["account"].Target;
        var scope = FactoryPreviewValidation.SqlScope(account, plan.Manifest.Identity.ProductKey, FactoryPublication.Container);
        return [FactoryPreviewValidation.Container(account, plan.Manifest.Identity.ProductKey, FactoryPublication.Container),
            Role(plan, true), Role(plan, false),
            FactoryPreviewValidation.SqlAssignment(account, plan.Bindings["auth"].PrincipalId!, scope, Role(plan, true)),
            FactoryPreviewValidation.SqlAssignment(account, plan.Bindings["council"].PrincipalId!, scope, Role(plan, false)),
            HostTarget(plan, false), HostTarget(plan, false) + "/authconfigs/current",
            HostTarget(plan, true), HostTarget(plan, true) + "/authconfigs/current"];
    }
    public static IReadOnlyList<(string Scope, string Principal)> MetadataScopes(PreviewPreparationPlan plan, Guid operation)
    {
        var scopes = new[] { HostTarget(plan, false), HostTarget(plan, true),
            Group(plan) + "/providers/microsoft.resources/deployments/publisher-" + operation.ToString("N") };
        return scopes.SelectMany(s => new[] { (s, plan.Bindings["auth"].PrincipalId!), (s, plan.Bindings["council"].PrincipalId!) })
            .Concat(new[] { "account", "auth", "council", "hosting" }.Select(k => (plan.Bindings[k].Target, plan.Bindings["auth"].PrincipalId!)))
            .Append((plan.Bindings["auth"].Target, plan.Bindings["council"].PrincipalId!)).ToArray();
    }
    public static void RequirePermissions(FactoryGitHubAuthority authority)
    {
        foreach (var (name, permission) in FactoryPublication.GitHubPermissions)
            if (!authority.ExpectedAppPermissions.TryGetValue(name, out var app) || !authority.ExpectedPermissions.TryGetValue(name, out var installation)
                || app != permission || installation != permission)
                throw new JsonException("GitHubAppAdministratorPrerequisiteBeforeInitialAuthorityApproval:" + name + ":" + permission
                    + "; existing pinned authority requires separate reviewed reconfiguration.");
    }
    public static FactoryPublisherPlan ReadPlan(FactoryPublisherCoordinator root, IReadOnlyDictionary<string, string> values)
    {
        if (root.Protocol != 1 || root.FactoryId == Guid.Empty || root.OperationId == Guid.Empty
            || !Enum.IsDefined(root.State) || root.PlanBytes is < 1 or > 524288
            || root.PlanChunks != (root.PlanBytes + 4095) / 4096 || root.ConfirmedEffects is < 0 or > 12)
            throw new JsonException("Invalid original publication rollout coordinator.");
        using var bytes = new MemoryStream(root.PlanBytes);
        for (var i = 0; i < root.PlanChunks; i++)
        {
            var chunk = Convert.FromBase64String(Parse<string>(values[ChunkKey(root.OperationId, i)]));
            if (chunk.Length != Math.Min(4096, root.PlanBytes - i * 4096)) throw new JsonException();
            bytes.Write(chunk);
        }
        var content = Encoding.UTF8.GetString(bytes.ToArray());
        if (FactoryAudit.TextHash(content) != root.PlanSha256) throw new JsonException();
        var plan = Parse<FactoryPublisherPlan>(content);
        FactoryLive.Validate(plan.Selection);
        RequirePermissions(plan.GitHubAuthority);
        var identity = plan.Deployment.Manifest.Identity;
        if (plan.Protocol != 1 || plan.Fingerprint != root.Fingerprint || plan.Selection.OperationId != root.OperationId
            || identity.FactoryId != root.FactoryId || identity.AzureActorId != root.ApprovedBy
            || plan.Activation.FactoryId != root.FactoryId || plan.Activation.ReviewedBy != root.ApprovedBy
            || plan.GitHubAuthority.FactoryId != root.FactoryId || plan.GitHubAuthority.RepositoryId != identity.RepositoryId
            || plan.GitHubAuthority.State != GitHubAuthorityState.Verified
            || plan.GitHubAuthority.AuthIdentityId != plan.Deployment.Bindings["auth"].Target
            || plan.GitHubAuthority.AuthPrincipalId != plan.Deployment.Bindings["auth"].PrincipalId
            || plan.GitHubAuthority.AuthClientId != plan.Deployment.Bindings["auth"].ClientId
            || plan.Automation.Mutation != RepositoryMutationClass.ProposalIssue || plan.Automation.State != RepositoryReviewState.Confirmed
            || plan.Automation.Declaration != RepositoryAutomationDeclaration.Clear || !plan.Automation.VisibleInventoryComplete
            || string.IsNullOrWhiteSpace(plan.Automation.DeclarationOwner))
            throw new JsonException("Exact operator-reviewed original publication authority required.");
        foreach (var (reference, key) in new[] { (plan.Approval, FactoryActivation.ApprovalKey(plan.Activation.OperationId)),
            (plan.Live, FactoryLive.RootKey), (plan.GitHub, Dsf.Core.Runtime.ProductConfigurationKeys.FactoryGitHubAuthority) })
            if (reference.FactoryId != root.FactoryId || reference.Store != AuditStoreKind.Governance || reference.Key != key
                || string.IsNullOrWhiteSpace(reference.NativeRevision) || !FactorySourceValidation.Hash(reference.Sha256))
                throw new JsonException("Original native governance reference required.");
        var steps = plan.Deployment.Steps;
        var targets = ResourceTargets(plan.Deployment);
        if (steps.Count != 12 || steps.Where((s, i) => s.Index != i).Any() || steps[0].Action != PreviewPreparationAction.WorkloadDeployment
            || steps[0].Target != Group(plan.Deployment) + "/providers/microsoft.resources/deployments/publisher-" + root.OperationId.ToString("N")
            || steps[0].BodySha256 != FactoryAudit.TextHash(plan.Deployment.WorkloadBody)
            || !steps[0].ResourceTargets.SequenceEqual(targets)) throw new JsonException("Exact publication resource set required.");
        var scopes = MetadataScopes(plan.Deployment, root.OperationId);
        foreach (var step in steps.Skip(1))
        {
            var grant = step.Grant ?? throw new JsonException();
            var expected = scopes[step.Index - 1];
            if (step.Action != PreviewPreparationAction.MetadataAssignment || grant.Scope != expected.Scope || grant.PrincipalId != expected.Principal
                || grant.RoleDefinitionId != plan.Selection.MetadataRoleDefinitionId || !grant.Actions.SequenceEqual(FactoryPreviewValidation.MetadataActions)
                || grant.Target != FactoryModelsValidation.Grant(grant.Scope, grant.RoleDefinitionId, grant.PrincipalId, grant.RoleRevision).Target
                || step.Target != grant.Target || !step.ResourceTargets.SequenceEqual([grant.Target])
                || step.BodySha256 != FactoryAudit.TextHash(FactoryPreviewPreparation.GrantBody(grant))) throw new JsonException("Exact scoped metadata grants required.");
        }
        return plan;
    }
    public static FactoryPublisherCapability ReadConfirmed(IReadOnlyDictionary<string, string> values, DateTimeOffset now)
    {
        var root = Parse<FactoryPublisherCoordinator>(values[RootKey]);
        var plan = ReadPlan(root, values);
        var fingerprint = plan.Fingerprint;
        if (root.State != FactoryPublisherState.PublicationPreparedAwaitingPromotion || root.ConfirmedEffects != plan.Deployment.Steps.Count
            || root.ApprovedAt < plan.Deployment.Manifest.Identity.CreatedAt || root.ApprovedAt > now) throw new JsonException("Original publication rollout incomplete.");
        var keys = new HashSet<string> { RootKey };
        for (var i = 0; i < root.PlanChunks; i++) keys.Add(ChunkKey(root.OperationId, i));
        var effects = new List<PreviewPreparationEffect>();
        var receipts = new List<PreviewPreparationReceipt>();
        foreach (var step in plan.Deployment.Steps)
        {
            var key = EffectKey(root.OperationId, step.Index);
            keys.Add(key);
            var effect = Parse<PreviewPreparationEffect>(values[key]);
            if (effect.Protocol != 1 || effect.FactoryId != root.FactoryId || effect.OperationId != root.OperationId
                || effect.PlanFingerprint != fingerprint || effect.Target != step.Target || effect.BodySha256 != step.BodySha256
                || effect.Index != step.Index || effect.State != PreviewPreparationEffectState.Confirmed || effect.RequestId == Guid.Empty
                || effect.IntendedAt < root.ApprovedAt || effect.IntendedAt > now || effect.ReceiptHashes.Count != step.ResourceTargets.Count
                || effect.Acknowledgment is not { StatusCode: 201 } ack || !FactorySourceValidation.Hash(ack.BodySha256)
                || ack.ObservedAt < effect.IntendedAt || ack.ObservedAt > now) throw new JsonException("Original native publication effect unconfirmed.");
            effects.Add(effect);
            for (var i = 0; i < step.ResourceTargets.Count; i++)
            {
                key = ReceiptKey(root.OperationId, step.Index, i);
                keys.Add(key);
                var receipt = Parse<PreviewPreparationReceipt>(values[key]);
                if (FactoryModelsValidation.Hash(receipt) != effect.ReceiptHashes[i] || receipt.Target != step.ResourceTargets[i]
                    || receipt.StatusCode is not (200 or 201) || !Guid.TryParse(receipt.RequestId, out var request) || request == Guid.Empty
                    || !Guid.TryParse(receipt.CorrelationId, out var correlation) || correlation == Guid.Empty
                    || step.Index == 0 && (receipt.DeploymentTarget != step.Target || receipt.CorrelationId != ack.DeploymentCorrelationId
                        || !FactoryTelemetryValidation.IsProviderOperationId(receipt.OperationId) || ack.ProvisioningState != "Succeeded")
                    || step.Index != 0 && (receipt.StatusCode != 201 || receipt.DeploymentTarget is not null || receipt.OperationId is not null
                        || receipt.RequestId != ack.RequestId || receipt.CorrelationId != ack.CorrelationId))
                    throw new JsonException("Original native publication receipt required.");
                receipts.Add(receipt);
            }
        }
        var observationKey = ObservationKey(root.OperationId);
        if (values.TryGetValue(observationKey, out var observation))
        {
            ValidateObservation(Parse<FactoryPublisherObservation>(observation), plan, root.ApprovedAt, now);
            keys.Add(observationKey);
        }
        if (root.NativeProviderProof is { } reference && (reference.FactoryId != root.FactoryId || reference.Store != AuditStoreKind.Governance
            || reference.Key != observationKey || string.IsNullOrWhiteSpace(reference.NativeRevision)
            || !values.TryGetValue(observationKey, out var retained) || reference.Sha256 != FactoryAudit.TextHash(retained)))
            throw new JsonException("Original native provider proof reference changed.");
        if (!keys.SetEquals(values.Keys) || receipts.Select(r => r.Target).Distinct().Count() != receipts.Count) throw new JsonException("Expanded publication namespace.");
        return new(plan, root, effects, receipts);
    }
    public static void ValidateObservation(FactoryPublisherObservation observed, FactoryPublisherPlan plan, DateTimeOffset after, DateTimeOffset now)
    {
        var proof = observed.GitHub;
        var authority = plan.GitHubAuthority;
        if (observed.Protocol != 1 || observed.FactoryId != plan.Deployment.Manifest.Identity.FactoryId
            || observed.CapabilityFingerprint != plan.Fingerprint || !FactorySourceValidation.Hash(observed.AuthorityRevision)
            || !FactorySourceValidation.Hash(observed.MutationReviewSha256)
            || proof.State != GitHubAuthorityState.Verified || proof.FactoryId != observed.FactoryId || proof.OperationId != authority.OperationId
            || proof.AppId != authority.AppId || proof.InstallationId != authority.InstallationId || proof.AccountId != authority.AccountId
            || proof.RepositoryId != authority.RepositoryId || proof.AuthPrincipalId != authority.AuthPrincipalId || proof.KeyVersionUri != authority.DestinationSecretUri
            || proof.Repository is not { Length: > 0 and <= 140 } || proof.DefaultBranch is not { Length: > 0 and <= 1024 }
            || System.Text.Encoding.UTF8.GetByteCount(proof.DefaultBranch) > 1024
            || proof.ObservedAt < after || proof.ObservedAt > now || proof.TokenExpiresAt <= proof.ObservedAt.AddMinutes(2)
            || !Permissions(proof.AppPermissions, authority.ExpectedAppPermissions) || !Permissions(proof.GrantedPermissions, authority.ExpectedPermissions)
            || proof.EffectivePermissions?.Count != 5 || new[] { "metadata", "contents", "actions", "repository_hooks" }
                .Any(p => proof.EffectivePermissions.GetValueOrDefault(p) != "read")
            || proof.EffectivePermissions.GetValueOrDefault("issues") != "write")
            throw new JsonException("Actual original native publisher identity and exact effective token scope required.");
        static bool Permissions(IReadOnlyDictionary<string, string>? actual, IReadOnlyDictionary<string, string> expected) =>
            actual?.Count == expected.Count && actual.All(p => expected.GetValueOrDefault(p.Key) == p.Value);
    }
    public static FactoryPublisherCapability ReadCreatedDeployment(IReadOnlyDictionary<string, string> values, DateTimeOffset now)
    {
        var root = Parse<FactoryPublisherCoordinator>(values[RootKey]);
        var plan = ReadPlan(root, values);
        if (root.State is not (FactoryPublisherState.Preparing or FactoryPublisherState.PublicationPreparedAwaitingPromotion)
            || root.ConfirmedEffects < 1 || root.ApprovedAt < plan.Deployment.Manifest.Identity.CreatedAt || root.ApprovedAt > now)
            throw new JsonException("Original publication creation is not acknowledged.");
        var step = plan.Deployment.Steps[0];
        var effect = Parse<PreviewPreparationEffect>(values[EffectKey(root.OperationId, 0)]);
        if (effect.Protocol != 1 || effect.FactoryId != root.FactoryId || effect.OperationId != root.OperationId || effect.Index != 0
            || effect.PlanFingerprint != plan.Fingerprint || effect.Target != step.Target || effect.BodySha256 != step.BodySha256
            || effect.State != PreviewPreparationEffectState.Confirmed || effect.RequestId == Guid.Empty
            || effect.IntendedAt < root.ApprovedAt || effect.IntendedAt > now || effect.ReceiptHashes.Count != step.ResourceTargets.Count
            || effect.Acknowledgment is not { StatusCode: 201, ProvisioningState: "Succeeded" } ack
            || !FactorySourceValidation.Hash(ack.BodySha256) || ack.ObservedAt < effect.IntendedAt || ack.ObservedAt > now)
            throw new JsonException("Original native publication creation acknowledgement required.");
        var receipts = new List<PreviewPreparationReceipt>();
        for (var i = 0; i < step.ResourceTargets.Count; i++)
        {
            var receipt = Parse<PreviewPreparationReceipt>(values[ReceiptKey(root.OperationId, 0, i)]);
            if (FactoryModelsValidation.Hash(receipt) != effect.ReceiptHashes[i] || receipt.Target != step.ResourceTargets[i]
                || receipt.DeploymentTarget != step.Target || receipt.CorrelationId != ack.DeploymentCorrelationId
                || receipt.StatusCode is not (200 or 201) || !Guid.TryParse(receipt.RequestId, out var request) || request == Guid.Empty
                || !FactoryTelemetryValidation.IsProviderOperationId(receipt.OperationId)) throw new JsonException();
            receipts.Add(receipt);
        }
        return new(plan, root, [effect], receipts);
    }
    public static void ValidateCreation(FactoryPublisherCapability capability, IReadOnlyDictionary<string, JsonElement> resources,
        JsonElement deployment, JsonElement operationSet)
    {
        var step = capability.Plan.Deployment.Steps[0];
        var effect = capability.Effects[0];
        var p = deployment.GetProperty("properties");
        if (p.GetProperty("provisioningState").GetString() != "Succeeded"
            || p.GetProperty("correlationId").GetString() != effect.Acknowledgment!.DeploymentCorrelationId
            || effect.DeploymentOutputsSha256 != FactoryModelsValidation.Hash(p.GetProperty("outputs"))
            || operationSet.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException("Original complete native publication deployment required.");
        var operations = operationSet.GetProperty("value").EnumerateArray().ToArray();
        if (operations.Length > 128 || operations.Select(o => o.GetProperty("operationId").GetString()).Distinct().Count() != operations.Length
            || operations.Any(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString() is not ("Create" or "Read" or "EvaluateDeploymentOutput"))
            || !operations.Where(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString() == "Create")
                .Select(o => o.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString()!)
                .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(step.ResourceTargets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new JsonException("Exact original publication creation set required.");
        foreach (var receipt in capability.Receipts.Where(r => r.DeploymentTarget is not null))
        {
            var resource = resources[receipt.Target];
            var properties = resource.GetProperty("properties");
            string incarnation, revision;
            if (receipt.Target.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal))
            {
                var host = FactoryHostedWorkloadPolicy.ObserveExactHost(resource, receipt.Target);
                incarnation = host.Incarnation;
                revision = host.Revision;
            }
            else
            {
                revision = FactoryModelsValidation.Hash(properties);
                if (receipt.Target.Contains("/sqlrole", StringComparison.Ordinal)) incarnation = "properties-sha256:" + revision;
                else
                {
                    var rid = properties.GetProperty("resource").GetProperty("_rid").GetString()!;
                    if (Convert.FromBase64String(rid.Replace('-', '/')).Length != 8) throw new JsonException();
                    incarnation = "cosmos-rid-sha256:" + FactoryAudit.TextHash(rid);
                }
            }
            var operation = operations.Single(o => o.GetProperty("operationId").GetString() == receipt.OperationId).GetProperty("properties");
            if (incarnation != receipt.Incarnation || revision != receipt.NativeRevision || receipt.DeploymentTarget != step.Target
                || receipt.CorrelationId != p.GetProperty("correlationId").GetString()
                || operation.GetProperty("provisioningOperation").GetString() != "Create" || operation.GetProperty("provisioningState").GetString() != "Succeeded"
                || !string.Equals(operation.GetProperty("targetResource").GetProperty("id").GetString(), receipt.Target, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(operation.GetProperty("trackingId").GetString(), out var tracking) || tracking.ToString() != receipt.RequestId
                || (operation.GetProperty("statusCode").GetString() switch { "Created" or "201" => 201, "OK" or "200" => 200, _ => 0 }) != receipt.StatusCode)
                throw new JsonException("Current publication incarnation or original native creation evidence changed.");
        }
    }
    public static void ValidateResources(FactoryPublisherPlan plan, IReadOnlyDictionary<string, JsonElement> resources, string endpoint)
    {
        var native = plan.Deployment;
        var identity = native.Manifest.Identity;
        var account = native.Bindings["account"].Target;
        var scope = FactoryPreviewValidation.SqlScope(account, identity.ProductKey, FactoryPublication.Container);
        var container = resources[FactoryPreviewValidation.Container(account, identity.ProductKey, FactoryPublication.Container)].GetProperty("properties");
        var data = container.GetProperty("resource");
        if (data.GetProperty("id").GetString() != FactoryPublication.Container
            || !FactoryModelsValidation.Strings(data.GetProperty("partitionKey"), "paths").SequenceEqual(["/product"])
            || data.GetProperty("partitionKey").GetProperty("kind").GetString() != "Hash"
            || data.GetProperty("partitionKey").GetProperty("version").GetInt32() != 2
            || data.TryGetProperty("defaultTtl", out _) || data.TryGetProperty("analyticalStorageTtl", out _)
            || container.TryGetProperty("options", out var options) && options.EnumerateObject().Any())
            throw new JsonException("Immutable publication evidence container without expiry required.");
        foreach (var writer in new[] { true, false })
        {
            var role = Role(native, writer);
            var p = resources[role].GetProperty("properties");
            if (p.GetProperty("type").GetString() != "CustomRole" || p.GetProperty("permissions").GetArrayLength() != 1
                || !FactoryModelsValidation.Strings(p, "assignableScopes").SequenceEqual([account], StringComparer.OrdinalIgnoreCase)
                || !FactoryModelsValidation.Strings(p.GetProperty("permissions")[0], "dataActions").Order(StringComparer.Ordinal)
                    .SequenceEqual((writer ? WriteDataActions : ReadDataActions).Order(StringComparer.Ordinal)))
                throw new JsonException("Exact AUTH writer or COUNCIL read-only publication role required.");
            var permission = p.GetProperty("permissions")[0];
            if (permission.EnumerateObject().Any(x => x.Name != "dataActions" && (x.Name != "notDataActions" || x.Value.GetArrayLength() != 0)))
                throw new JsonException("Expanded publication permission.");
            var principal = native.Bindings[writer ? "auth" : "council"].PrincipalId!;
            p = resources[FactoryPreviewValidation.SqlAssignment(account, principal, scope, role)].GetProperty("properties");
            if (p.GetProperty("principalId").GetString() != principal || !string.Equals(p.GetProperty("scope").GetString(), scope, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(p.GetProperty("roleDefinitionId").GetString(), role, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("Exact original identity/container assignment required.");
        }
        foreach (var context in new[] { false, true })
        {
            var host = HostTarget(native, context);
            var caller = native.Bindings[context ? "auth" : "council"];
            var actual = FactoryHostedWorkloadPolicy.Validate(resources[host], resources[host + "/authconfigs/current"],
                native.Bindings[context ? "council" : "auth"], native.Bindings["hosting"], plan.Selection.RuntimeImage,
                context ? ContextVerb : Verb, identity.ProductKey, $"https://dsfo-{identity.FactoryId:N}.azconfig.io",
                native.Manifest.ProfilePin, identity.TenantId, plan.Selection.AudienceClientId,
                context ? [caller.PrincipalId!, identity.AzureActorId] : [caller.PrincipalId!]);
            var policy = resources[host + "/authconfigs/current"].GetProperty("properties").GetProperty("identityProviders")
                .GetProperty("azureActiveDirectory").GetProperty("validation").GetProperty("defaultAuthorizationPolicy");
            var applications = policy.TryGetProperty("allowedApplications", out _) ? FactoryModelsValidation.Strings(policy, "allowedApplications") : [];
            if (!applications.SequenceEqual(context ? [] : [caller.ClientId!]))
                throw new JsonException("Exact native publisher caller application required.");
            var template = resources[host].GetProperty("properties").GetProperty("template");
            var capacity = template.GetProperty("containers")[0].GetProperty("resources");
            if (capacity.GetProperty("cpu").GetDouble() != 0.25 || capacity.GetProperty("memory").GetString() != "0.5Gi"
                || template.GetProperty("scale").GetProperty("minReplicas").GetInt32() != 1
                || template.GetProperty("scale").GetProperty("maxReplicas").GetInt32() != 1 || context && actual != endpoint)
                throw new JsonException("Exact pinned private publication host required.");
        }
    }
    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
}
