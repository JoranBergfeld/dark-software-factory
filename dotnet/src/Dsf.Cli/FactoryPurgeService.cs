using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryPurgeService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryPurgeResult> ExecuteAsync(
        FactoryReservationResult admission, Guid operation, string? approval, string? auditExport,
        CancellationToken token)
    {
        if (admission.Identity is not { } identity || admission.Revision is null)
            return new(FactoryPurgeState.Blocked, "Trusted owner recovery identity and current reservation revision required.");
        var owner = Store(identity.OwnerEndpoint, identity);
        var ownerRecord = await owner.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey), token);
        if (ownerRecord is null || ownerRecord.ETag.ToString() != admission.Revision)
            return new(FactoryPurgeState.Blocked, "Owner reservation changed before purge review.");
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(ownerRecord.Value);
        if (anchor.Identity != identity) throw new JsonException();
        if (anchor.Offboarding?.State != FactoryOffboardingState.Detached)
            return new(FactoryPurgeState.Blocked,
                "Completed council detach is required before separate infrastructure purge review.");
        if (admission.Foundation?.ProductEndpoint is not { } endpoint)
            return new(FactoryPurgeState.Blocked,
                "Retained product authority and audit stores are required for reviewed purge; partial onboarding remains preserved.");

        var product = Store(endpoint, identity);
        var audit = new FactoryAuditService(azure, http, clock);
        FactoryPurgePlan plan;
        FactoryPurgeRequest? request = null;
        AuditDisposition auditDisposition;
        if (anchor.Purge is { } retained)
        {
            if (retained.OperationId != operation)
                return new(FactoryPurgeState.Blocked,
                    "Another durable purge operation already owns this retired-lifecycle transition; no replacement.");
            plan = await ReadPlanAsync(owner, identity, retained, token);
            request = await ReadRequestAsync(owner, identity, retained, token);
            auditDisposition = new(request.AuditDisposition, []);
            if (string.IsNullOrWhiteSpace(approval))
                return Result(FactoryPurgeState.ReviewRequired,
                    "Purge recovery requires renewed approval of the retained exact plan; no delete replay.",
                    plan, request, false);
        }
        else
        {
            var authority = await product.ReadFactoryAuthorityAsync(token);
            AuditGraph? graph = null;
            if (!string.IsNullOrWhiteSpace(auditExport)
                || await product.ReadAsync(FactoryAuditRetention.PolicyKey, token) is not null)
                graph = await audit.ReadAsync(admission, token);
            auditDisposition = graph is null
                ? new("retained audit",
                    ["Audit stores require a current verified private export or reached explicitly approved eligible expiry; ordinary unused-analysis expiry cannot erase required evidence."])
                : await AuditDispositionAsync(admission, product, graph, auditExport, audit, token);
            plan = await BuildPlanAsync(owner, product, identity, operation, authority, graph,
                auditDisposition, admission, token);
            if (string.IsNullOrWhiteSpace(approval))
                return Result(FactoryPurgeState.ReviewRequired,
                    "Separate destructive purge review required; no infrastructure deletion or identity retirement dispatched.",
                    plan, null, false);
            if (approval != plan.Fingerprint)
                return Result(FactoryPurgeState.Blocked,
                    "Approval differs from the exact current purge plan; no effect dispatched.", plan, null, false);
            if (plan.BlockedDeletions.Count != 0)
                return Result(FactoryPurgeState.Blocked,
                    "Current ownership, cascade, unresolved effect or audit disposition blocks deletion; nothing was force-adopted.",
                    plan, null, false);

            request = new FactoryPurgeRequest(1, FactoryPurge.Kind, operation, identity.FactoryId,
                plan.Fingerprint, plan.AuthorityRevision, plan.AuditRevision,
                auditDisposition.Description, identity.AzureActorId, clock.GetUtcNow());
            await CreateExactAsync(owner, FactoryPurge.PlanKey(operation),
                SerializePlan(plan), token);
            await CreateExactAsync(owner, FactoryPurge.HistoryKey(operation),
                JsonSerializer.Serialize(request, FactoryGitHubJson.Options), token);
            var locked = anchor with
            {
                Purge = new(operation, FactoryPurgeState.Purging, plan.Fingerprint,
                    plan.AuthorityRevision, plan.AuditRevision, request.RequestedAt),
            };
            ownerRecord = await SetExactAsync(owner, ownerRecord,
                FactoryReservationRecords.Serialize(locked), token);
            anchor = locked;
        }

        if (approval != plan.Fingerprint)
            return Result(FactoryPurgeState.Blocked,
                "Approval differs from the retained exact purge plan; no delete replay.", plan, request, false);
        if (request is null) throw new JsonException();

        var deleted = new List<string>();
        var blocked = new List<string>();
        var uncertain = false;
        var arm = new FactoryPreparationArm(azure, http, identity);
        var ordered = plan.ProposedDeletions
            .OrderBy(resource => resource.AuditBacking ? 1 : 0)
            .ThenByDescending(resource => resource.ResourceId.Count(character => character == '/'))
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var resource = ordered[index];
            var recorded = await owner.ReadAsync(FactoryPurge.EffectKey(operation, index), token);
            var requiresDispatchBoundary = recorded is null
                || (JsonSerializer.Deserialize<FactoryPurgeDeleteEffect>(
                    recorded.Value, FactoryGitHubJson.Options) ?? throw new JsonException()).State
                    == AuditDeleteState.Intended;
            if (requiresDispatchBoundary)
            {
                var boundary = resource.AuditBacking
                    ? await RequireFinalBoundaryAsync(owner, product, admission, anchor, plan,
                        request, auditExport, audit, token)
                    : await ValidateBoundaryAsync(owner, product, admission, anchor, plan,
                        auditExport, audit, token);
                if (boundary is not null)
                {
                    blocked.Add(boundary);
                    break;
                }
            }
            var outcome = await ApplyEffectAsync(owner, arm, identity, operation, index,
                resource, plan, token);
            (outcome.State == AuditDeleteState.Confirmed ? deleted : blocked).Add(outcome.Detail);
            uncertain |= outcome.State == AuditDeleteState.Unknown;
            if (outcome.State != AuditDeleteState.Confirmed) break;
        }

        if (blocked.Count != 0)
            return Result(uncertain ? FactoryPurgeState.Unknown : FactoryPurgeState.Partial,
                "Safe independent deletions are recorded; unresolved current ownership or provider outcomes prevent completed purge and retirement.",
                plan, request, false) with
            {
                Deleted = deleted,
                Blocked = plan.BlockedDeletions.Concat(blocked).Distinct(StringComparer.Ordinal).ToArray(),
                Preserved = plan.PreservedAssets.Concat(blocked).Distinct(StringComparer.Ordinal).ToArray(),
            };

        if (!ordered.Any(resource => resource.AuditBacking))
        {
            var boundary = await ValidateBoundaryAsync(owner, product, admission, anchor, plan,
                auditExport, audit, token);
            if (boundary is not null)
                return Result(FactoryPurgeState.Partial,
                    "All planned effects completed, but current authority or audit roots changed before retirement.",
                    plan, request, false) with { Deleted = deleted, Blocked = [boundary] };
        }

        var completion = await ValidateCompletionAsync(
            owner, identity, anchor, plan, request, ordered, token);
        if (completion is not null)
            return Result(FactoryPurgeState.Blocked,
                "Deletion completion or retirement authority changed; identity remains non-retired for original-operation recovery.",
                plan, request, false) with { Deleted = deleted, Blocked = [completion] };

        var retirement = new FactoryRetirementPointer(1, FactoryPurge.RetirementKind, identity.FactoryId,
            operation, identity.ProductKey, plan.Fingerprint, auditDisposition.Description,
            plan.AuditRevision, identity.AzureActorId, clock.GetUtcNow());
        var currentOwner = await owner.ReadAsync(ownerRecord.Key, token) ?? throw new JsonException();
        var currentAnchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(currentOwner.Value);
        if (currentAnchor.Identity != identity || currentAnchor.Purge != anchor.Purge
            || currentAnchor.Offboarding?.State != FactoryOffboardingState.Detached)
            return Result(FactoryPurgeState.Blocked,
                "Owner retirement authority changed after deletion; identity remains non-retired and requires original-operation recovery.",
                plan, request, false) with { Deleted = deleted, Blocked = ["owner retirement authority changed"] };
        await SetExactAsync(owner, currentOwner, FactoryReservationRecords.Serialize(retirement), token);
        return Result(FactoryPurgeState.Purged,
            "Exact current owned retained infrastructure was removed where confirmed; immutable factory identity permanently retired.",
            plan, request, true) with { Deleted = deleted };
    }

    private static async Task<string?> ValidateCompletionAsync(
        OwnerReservationStore owner, FactoryReservationIdentity identity,
        FactoryReservationAnchor expectedAnchor, FactoryPurgePlan plan, FactoryPurgeRequest request,
        IReadOnlyList<FactoryPurgeResource> ordered, CancellationToken token)
    {
        var lifecycle = await ValidateOwnerLifecycleAsync(owner, expectedAnchor, token);
        if (lifecycle is not null) return lifecycle;
        var retainedRequest = await owner.ReadAsync(FactoryPurge.HistoryKey(request.OperationId), token);
        if (retainedRequest is null
            || retainedRequest.Value != JsonSerializer.Serialize(request, FactoryGitHubJson.Options))
            return "Approved purge request changed or became unreadable before retirement.";
        if (ordered.Any(resource => resource.AuditBacking))
        {
            var retainedBoundary = await owner.ReadAsync(FactoryPurge.BoundaryKey(request.OperationId), token);
            if (retainedBoundary is null) return "Final audit-store boundary is missing before retirement.";
            FactoryPurgeBoundary boundary;
            try
            {
                boundary = JsonSerializer.Deserialize<FactoryPurgeBoundary>(
                    retainedBoundary.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return "Final audit-store boundary is unreadable before retirement.";
            }
            if (boundary.Protocol != 1 || boundary.FactoryId != identity.FactoryId
                || boundary.OperationId != request.OperationId
                || boundary.PlanFingerprint != plan.Fingerprint
                || boundary.AuthorityRevision != plan.AuthorityRevision
                || boundary.AuditRevision != plan.AuditRevision
                || boundary.AuditDisposition != request.AuditDisposition
                || boundary.VerifiedAt < request.RequestedAt)
                return "Final audit-store boundary changed before retirement.";
        }
        var effects = await owner.ReadNamespaceAsync(
            FactoryPurge.HistoryPrefix + request.OperationId.ToString("N") + "/effects/", token,
            maximumRecords: ordered.Count + 1);
        if (effects.Count != ordered.Count)
            return "Purge effect set is incomplete or contains an unrecognized effect before retirement.";
        for (var index = 0; index < ordered.Count; index++)
        {
            var key = FactoryPurge.EffectKey(request.OperationId, index);
            if (!effects.TryGetValue(key, out var record))
                return "Purge effect is missing before retirement: " + key + ".";
            FactoryPurgeDeleteEffect effect;
            try
            {
                effect = JsonSerializer.Deserialize<FactoryPurgeDeleteEffect>(
                    record.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return "Purge effect is unreadable before retirement: " + key + ".";
            }
            if (effect.Protocol != 1 || effect.FactoryId != identity.FactoryId
                || effect.OperationId != request.OperationId || effect.Index != index
                || effect.Target != ordered[index] || effect.State != AuditDeleteState.Confirmed)
                return "Purge effect is unresolved or changed before retirement: " + key + ".";
        }
        var claims = await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token);
        if (claims.Values.Any(record =>
                (record.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "repositories/",
                     StringComparison.Ordinal)
                 || record.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "resources/",
                     StringComparison.Ordinal))
                && FactoryReservationRecords.TryIdentify(record.Key, record.Label, record.Value, out var claim)
                && claim == identity))
            return "An owner repository or resource claim reappeared before retirement.";
        return null;
    }

    private async Task<FactoryPurgePlan> BuildPlanAsync(
        OwnerReservationStore owner, OwnerReservationStore product, FactoryReservationIdentity identity,
        Guid operation, IReadOnlyDictionary<string, ConfigurationSetting> authority, AuditGraph? graph,
        AuditDisposition auditDisposition, FactoryReservationResult admission, CancellationToken token)
    {
        var records = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var pair in await owner.ReadNamespaceAsync("dsf/foundation/", token, 256, FactoryAudit.MaximumBytes, 1024))
            records[pair.Key] = pair.Value;
        foreach (var pair in authority) records[pair.Key] = pair.Value;
        var candidates = new List<(string Key, FoundationOperation Operation)>();
        foreach (var record in records.Values)
        {
            try
            {
                var effect = JsonSerializer.Deserialize<FoundationOperation>(record.Value, FactoryGitHubJson.Options);
                if (effect is
                    {
                        State: FoundationOperationState.Completed,
                        Disposition: FoundationResourceDisposition.Created,
                        ResourceId: not null,
                        Incarnation: not null,
                    } && effect.FactoryId == identity.FactoryId
                      && !effect.ResourceId.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.OrdinalIgnoreCase)
                    && effect.ResourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                    && !effect.ResourceId.Contains("/secrets/", StringComparison.OrdinalIgnoreCase))
                    candidates.Add((record.Key, effect));
            }
            catch (JsonException)
            {
                try
                {
                    using var document = JsonDocument.Parse(record.Value);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("kind", out var kind)
                        && kind.ValueKind == JsonValueKind.String
                        && kind.GetString() is { } value
                        && value.StartsWith("decide-", StringComparison.Ordinal)
                        && (record.Key.EndsWith("-operation", StringComparison.Ordinal)
                            || record.Key.Contains("/operations/", StringComparison.Ordinal)
                            || record.Key.Contains("/effects/", StringComparison.Ordinal))
                        && !KnownAuthorityKinds.Contains(value))
                        unreadable.Add("Unrecognized authority record blocks purge: " + record.Key + ".");
                }
                catch (JsonException) { unreadable.Add("Unreadable authority record blocks purge: " + record.Key + "."); }
            }
        }

        var proposed = new List<FactoryPurgeResource>();
        var blocked = new List<string>(auditDisposition.Blockers);
        blocked.AddRange(unreadable);
        blocked.AddRange(PendingEffects(authority));
        var ownerClaims = await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token);
        foreach (var claim in ownerClaims.Values.Where(record =>
                     (record.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "repositories/", StringComparison.Ordinal)
                      || record.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "resources/", StringComparison.Ordinal))
                     && FactoryReservationRecords.TryIdentify(record.Key, record.Label, record.Value, out var claimIdentity)
                     && claimIdentity == identity))
            blocked.Add("Unresolved owner claim remains held: " + claim.Key + ".");
        var priorPurges = await owner.ReadNamespaceAsync(FactoryPurge.HistoryPrefix, token, 1024);
        foreach (var prior in priorPurges.Values.Where(record =>
                     record.Key.EndsWith("/request", StringComparison.Ordinal)))
        {
            try
            {
                var priorRequest = JsonSerializer.Deserialize<FactoryPurgeRequest>(
                    prior.Value, FactoryGitHubJson.Options);
                if (priorRequest?.FactoryId == identity.FactoryId)
                    blocked.Add("Unresolved prior purge claim remains held: " + prior.Key + ".");
            }
            catch (JsonException)
            {
                blocked.Add("Unreadable prior purge claim blocks replacement: " + prior.Key + ".");
            }
        }
        var arm = new FactoryPreparationArm(azure, http, identity);
        foreach (var candidate in candidates.DistinctBy(item => item.Operation.ResourceId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var effect = candidate.Operation;
            var resource = new FactoryPurgeResource(candidate.Key, effect.ResourceId!, ResourceType(effect.ResourceId!),
                effect.ApiVersion, identity.FactoryId, effect.OperationId, effect.ClientRequestId,
                effect.StartedAt, effect.Incarnation!, Parent(effect.ResourceId!),
                IsAuditBacking(effect.ResourceId!, graph));
            if (resource.ResourceType.Equals("Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("Broad resource-group deletion is forbidden; preserved: " + resource.ResourceId + ".");
                continue;
            }
            var current = await InspectAsync(arm, resource, token);
            if (current.Eligible) proposed.Add(resource);
            else if (!current.Absent) blocked.Add(current.Detail + " Evidence=" + resource.AuthorityKey
                + "; incarnation=" + resource.Incarnation + ".");
        }
        var provenIds = candidates.Select(candidate => candidate.Operation.ResourceId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in provenIds.Select(ResourceGroup).Where(value => value is not null)
                     .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await arm.SendAsync(HttpMethod.Get, group + "/resources", "2021-04-01", token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    BlockCascadeParents(group, ["inventory unavailable"]);
                    continue;
                }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token),
                    new JsonDocumentOptions { AllowDuplicateProperties = false });
                var current = document.RootElement.GetProperty("value").EnumerateArray()
                    .Select(resource => resource.GetProperty("id").GetString() ?? throw new JsonException())
                    .ToArray();
                var unowned = current.Where(id => !provenIds.Contains(id)).ToArray();
                BlockCascadeParents(group, unowned);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException
                or InvalidOperationException or KeyNotFoundException)
            {
                BlockCascadeParents(group, ["inventory unavailable"]);
            }
        }
        if (proposed.Count == 0)
            blocked.Add("No current provenance-proven retained factory resources are deletion-eligible.");

        var preserved = new[]
        {
            "application resources, identities, configuration and diagnostics",
            "repository content, settings, labels, proposals and charter work",
            "shared owner App Configuration, Key Vault, GitHub App key and installation",
            "reused model, network and evidence prerequisites",
            "unresolved claims, effects and unproven or human-modified assets",
            "parent/resource groups unless every current child is independently proven and removed",
        };
        var revision = Revision(records.Values);
        var draft = new FactoryPurgePlan(1, FactoryPurge.PlanKind, operation, identity.FactoryId,
            identity.ProductKey, revision, graph?.Revision ?? "unverified", preserved,
            proposed.OrderBy(resource => resource.ResourceId, StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ["provider soft-delete/recoverable custody and already-issued tokens are not physical erasure guarantees"], "");
        return draft with { Fingerprint = FactoryPurge.Fingerprint(draft) };

        void BlockCascadeParents(string group, IReadOnlyList<string> unowned)
        {
            foreach (var parent in proposed.Where(candidate =>
                         unowned.Any(child => child.StartsWith(candidate.ResourceId + "/", StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
            {
                proposed.Remove(parent);
                blocked.Add("Parent deletion blocked by unowned or unproven child "
                    + unowned.First(child => child.StartsWith(parent.ResourceId + "/", StringComparison.OrdinalIgnoreCase))
                    + "; preserved parent " + parent.ResourceId + ".");
            }
            if (unowned.Contains("inventory unavailable", StringComparer.Ordinal))
                foreach (var parent in proposed.Where(candidate => IsCascadeParent(candidate.ResourceType)).ToArray())
                {
                    proposed.Remove(parent);
                    blocked.Add("Current transitive child inventory unavailable; preserved parent "
                        + parent.ResourceId + " in " + group + ".");
                }
        }
    }

    private async Task<AuditDisposition> AuditDispositionAsync(
        FactoryReservationResult admission, OwnerReservationStore product, AuditGraph graph,
        string? export, FactoryAuditService audit, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(export))
        {
            try
            {
                var verified = await audit.ExportAsync(admission, graph, export, null, true, token);
                if (verified.State == FactoryAuditState.Verified)
                    return new("verified private export " + verified.Receipt!.ManifestSha256, []);
            }
            catch (Exception e) when (e is FactoryAuditException or JsonException or IOException
                or UnauthorizedAccessException)
            {
                return new("invalid export",
                    ["Audit export is stale, incomplete, corrupt, inaccessible or not reference-closed against current roots."]);
            }
        }
        var policyRecord = await product.ReadAsync(FactoryAuditRetention.PolicyKey, token);
        if (policyRecord is not null)
        {
            try
            {
                var policy = JsonSerializer.Deserialize<AuditRetentionApproval>(
                    policyRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
                var expiry = FactoryPurgeAuditDisposition.Evaluate(graph, policy, clock.GetUtcNow());
                if (expiry.Eligible) return new(expiry.Detail, []);
                return new("retained audit", [expiry.Detail]);
            }
            catch (JsonException) { }
        }
        return new("retained audit",
            ["Audit stores require a current verified private export or reached explicitly approved eligible expiry; ordinary unused-analysis expiry cannot erase required evidence."]);
    }

    private async Task<string?> ValidateBoundaryAsync(
        OwnerReservationStore owner, OwnerReservationStore product, FactoryReservationResult admission,
        FactoryReservationAnchor expectedAnchor, FactoryPurgePlan plan, string? auditExport,
        FactoryAuditService audit, CancellationToken token)
    {
        var root = await owner.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(expectedAnchor.Identity.ProductKey), token);
        if (root is null) return "Owner purge lifecycle authority is missing.";
        FactoryReservationAnchor current;
        try { current = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value); }
        catch (JsonException) { return "Owner purge lifecycle authority is unreadable or retired unexpectedly."; }
        if (current.Identity != expectedAnchor.Identity || current.Offboarding?.State != FactoryOffboardingState.Detached
            || current.Purge != expectedAnchor.Purge)
            return "Owner purge lifecycle authority changed; no further effect dispatched.";
        if (await CurrentAuthorityRevisionAsync(owner, product, token) != plan.AuthorityRevision)
            return "Current factory authority revision changed; no further effect dispatched.";
        var graph = await audit.ReadAsync(admission, token);
        if (graph.Revision != plan.AuditRevision)
            return "Current audit roots changed; stale export cannot authorize further deletion.";
        var disposition = await AuditDispositionAsync(admission, product, graph, auditExport, audit, token);
        return disposition.Blockers.Count == 0 ? null
            : "Current audit disposition is no longer deletion-eligible: " + string.Join(" ", disposition.Blockers);
    }

    private async Task<string?> RequireFinalBoundaryAsync(
        OwnerReservationStore owner, OwnerReservationStore product, FactoryReservationResult admission,
        FactoryReservationAnchor expectedAnchor, FactoryPurgePlan plan, FactoryPurgeRequest request,
        string? auditExport, FactoryAuditService audit, CancellationToken token)
    {
        var key = FactoryPurge.BoundaryKey(request.OperationId);
        var retained = await owner.ReadAsync(key, token);
        if (retained is not null)
        {
            var boundary = JsonSerializer.Deserialize<FactoryPurgeBoundary>(
                retained.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            if (boundary.Protocol != 1 || boundary.FactoryId != expectedAnchor.Identity.FactoryId
                || boundary.OperationId != request.OperationId
                || boundary.PlanFingerprint != plan.Fingerprint
                || boundary.AuthorityRevision != plan.AuthorityRevision
                || boundary.AuditRevision != plan.AuditRevision
                || boundary.AuditDisposition != request.AuditDisposition
                || boundary.VerifiedAt < request.RequestedAt
                || boundary.VerifiedAt > clock.GetUtcNow())
                return "Retained final audit-store boundary differs from the approved purge.";
            return await ValidateOwnerLifecycleAsync(owner, expectedAnchor, token);
        }
        var current = await ValidateBoundaryAsync(owner, product, admission, expectedAnchor, plan,
            auditExport, audit, token);
        if (current is not null) return current;
        var value = new FactoryPurgeBoundary(1, expectedAnchor.Identity.FactoryId,
            request.OperationId, plan.Fingerprint, plan.AuthorityRevision, plan.AuditRevision,
            request.AuditDisposition, clock.GetUtcNow());
        await CreateExactAsync(owner, key, JsonSerializer.Serialize(value, FactoryGitHubJson.Options), token);
        return null;
    }

    private static async Task<string?> ValidateOwnerLifecycleAsync(
        OwnerReservationStore owner, FactoryReservationAnchor expected, CancellationToken token)
    {
        var root = await owner.ReadAsync(
            ProductConfigurationKeys.OwnerProductClaim(expected.Identity.ProductKey), token);
        if (root is null) return "Owner purge lifecycle authority is missing.";
        try
        {
            var current = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value);
            return current.Identity == expected.Identity
                && current.Offboarding?.State == FactoryOffboardingState.Detached
                && current.Purge == expected.Purge
                ? null : "Owner purge lifecycle authority changed; no further effect dispatched.";
        }
        catch (JsonException)
        {
            return "Owner purge lifecycle authority is unreadable or retired unexpectedly.";
        }
    }

    internal static async Task<FactoryPurgePlan> ReadPlanAsync(
        OwnerReservationStore owner, FactoryReservationIdentity identity,
        FactoryPurgePointer pointer, CancellationToken token)
    {
        var record = await owner.ReadAsync(FactoryPurge.PlanKey(pointer.OperationId), token)
            ?? throw new JsonException();
        var envelope = JsonSerializer.Deserialize<PurgePlanEnvelope>(
            record.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
        if (envelope.Protocol != 1 || envelope.Kind != FactoryPurge.PlanKind) throw new JsonException();
        var compressed = Convert.FromBase64String(envelope.Data);
        if (compressed.Length > 64 * 1024) throw new JsonException();
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        if (output.Length > 1024 * 1024) throw new JsonException();
        var plan = JsonSerializer.Deserialize<FactoryPurgePlan>(
            output.ToArray(), FactoryGitHubJson.Options) ?? throw new JsonException();
        if (plan.Protocol != 1 || plan.Kind != FactoryPurge.PlanKind
            || plan.OperationId != pointer.OperationId || plan.FactoryId != identity.FactoryId
            || plan.ProductKey != identity.ProductKey || plan.Fingerprint != pointer.PlanFingerprint
            || plan.AuthorityRevision != pointer.AuthorityRevision || plan.AuditRevision != pointer.AuditRevision
            || FactoryPurge.Fingerprint(plan) != plan.Fingerprint)
            throw new JsonException();
        return plan;
    }

    private static string SerializePlan(FactoryPurgePlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, FactoryGitHubJson.Options);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(bytes);
        return JsonSerializer.Serialize(new PurgePlanEnvelope(
            1, FactoryPurge.PlanKind, Convert.ToBase64String(output.ToArray())), FactoryGitHubJson.Options);
    }

    internal static async Task<FactoryPurgeRequest> ReadRequestAsync(
        OwnerReservationStore owner, FactoryReservationIdentity identity,
        FactoryPurgePointer pointer, CancellationToken token)
    {
        var record = await owner.ReadAsync(FactoryPurge.HistoryKey(pointer.OperationId), token)
            ?? throw new JsonException();
        var request = JsonSerializer.Deserialize<FactoryPurgeRequest>(record.Value, FactoryGitHubJson.Options)
            ?? throw new JsonException();
        if (request.Protocol != 1 || request.Kind != FactoryPurge.Kind
            || request.OperationId != pointer.OperationId || request.FactoryId != identity.FactoryId
            || request.PlanFingerprint != pointer.PlanFingerprint
            || request.AuthorityRevision != pointer.AuthorityRevision
            || request.AuditRevision != pointer.AuditRevision
            || string.IsNullOrWhiteSpace(request.AuditDisposition)
            || request.ApprovedBy != identity.AzureActorId
            || request.RequestedAt != pointer.RequestedAt)
            throw new JsonException();
        return request;
    }

    private static async Task<ConfigurationSetting> CreateExactAsync(
        OwnerReservationStore store, string key, string value, CancellationToken token)
    {
        try { return await store.WriteAsync(key, value, null, token); }
        catch (ReservationStoreException)
        {
            var existing = await store.ReadAsync(key, CancellationToken.None);
            if (existing?.Value == value) return existing;
            throw;
        }
    }

    private static async Task<ConfigurationSetting> SetExactAsync(
        OwnerReservationStore store, ConfigurationSetting expected, string value, CancellationToken token)
    {
        try { return await store.WriteAsync(expected.Key, value, expected.ETag, token); }
        catch (ReservationStoreException)
        {
            var existing = await store.ReadAsync(expected.Key, CancellationToken.None);
            if (existing?.Value == value) return existing;
            throw;
        }
    }

    private async Task<string> CurrentAuthorityRevisionAsync(
        OwnerReservationStore owner, OwnerReservationStore product, CancellationToken token)
    {
        var records = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        foreach (var pair in await owner.ReadNamespaceAsync("dsf/foundation/", token, 256, FactoryAudit.MaximumBytes, 1024))
            records[pair.Key] = pair.Value;
        foreach (var pair in await product.ReadFactoryAuthorityAsync(token))
            records[pair.Key] = pair.Value;
        return Revision(records.Values);
    }

    private async Task<(AuditDeleteState State, string Detail)> ApplyEffectAsync(
        OwnerReservationStore owner, FactoryPreparationArm arm, FactoryReservationIdentity identity,
        Guid operation, int index, FactoryPurgeResource resource, FactoryPurgePlan plan,
        CancellationToken token)
    {
        var key = FactoryPurge.EffectKey(operation, index);
        var effectRecord = await owner.ReadAsync(key, token);
        FactoryPurgeDeleteEffect effect;
        if (effectRecord is not null)
        {
            effect = JsonSerializer.Deserialize<FactoryPurgeDeleteEffect>(
                effectRecord.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            if (effect.Protocol != 1 || effect.FactoryId != identity.FactoryId
                || effect.OperationId != operation || effect.Index != index || effect.Target != resource)
                throw new JsonException();
            if (effect.State == AuditDeleteState.Confirmed)
                return (effect.State, effect.Detail ?? "Previously confirmed deletion " + resource.ResourceId + ".");
            if (effect.State == AuditDeleteState.KnownNoEffect)
                return (effect.State, effect.Detail ?? "Previously preserved " + resource.ResourceId + ".");
            if (effect.State is AuditDeleteState.Dispatched or AuditDeleteState.Unknown)
            {
                var reconciled = await InspectAsync(arm, resource, token);
                if (reconciled.Absent)
                    return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.Confirmed,
                        "Reconciled exact owned resource as absent after uncertain deletion " + resource.ResourceId + ".");
                if (!reconciled.Eligible)
                    return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.KnownNoEffect,
                        reconciled.Detail + " Original uncertain delete was not repeated.");
                return (AuditDeleteState.Unknown,
                    "Deletion remains uncertain for exact original resource " + resource.ResourceId
                    + "; original dispatch is never repeated while that incarnation exists.");
            }
        }
        else
        {
            effect = new(1, identity.FactoryId, operation, index, resource,
                AuditDeleteState.Intended, clock.GetUtcNow());
            effectRecord = await CreateExactAsync(owner, key,
                JsonSerializer.Serialize(effect, FactoryGitHubJson.Options), token);
        }

        var current = await InspectAsync(arm, resource, token);
        if (current.Absent)
            return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.Confirmed,
                "Confirmed already absent exact owned resource " + resource.ResourceId + ".");
        if (!current.Eligible)
            return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.KnownNoEffect,
                current.Detail);
        var cascade = await InspectCascadeAsync(arm, resource, plan, token);
        if (cascade is not null)
            return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.KnownNoEffect, cascade);

        effect = effect with { State = AuditDeleteState.Dispatched, UpdatedAt = clock.GetUtcNow() };
        effectRecord = await SetExactAsync(owner, effectRecord,
            JsonSerializer.Serialize(effect, FactoryGitHubJson.Options), token);
        try
        {
            using var deletedResponse = await arm.SendAsync(
                HttpMethod.Delete, resource.ResourceId, resource.ApiVersion, token);
            if (deletedResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.KnownNoEffect,
                    "Deletion denied; preserved exact owned resource " + resource.ResourceId + ".");
            if (deletedResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted
                or HttpStatusCode.NoContent or HttpStatusCode.NotFound))
                return await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.Unknown,
                    "Deletion response uncertain for exact owned resource " + resource.ResourceId + ".");
            using var confirmed = await arm.SendAsync(HttpMethod.Get, resource.ResourceId, resource.ApiVersion, token);
            return confirmed.StatusCode == HttpStatusCode.NotFound
                ? await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.Confirmed,
                    "Deleted exact current owned resource " + resource.ResourceId + ".")
                : await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.Unknown,
                    "Deletion not confirmed absent for exact owned resource " + resource.ResourceId + ".");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            await CloseEffectAsync(owner, effectRecord, effect, AuditDeleteState.Unknown,
                "Deletion outcome uncertain for exact owned resource " + resource.ResourceId + ".",
                CancellationToken.None);
            if (e is OperationCanceledException && token.IsCancellationRequested) throw;
            return (AuditDeleteState.Unknown,
                "Deletion outcome uncertain for exact owned resource " + resource.ResourceId + ".");
        }
    }

    private async Task<(AuditDeleteState State, string Detail)> CloseEffectAsync(
        OwnerReservationStore owner, ConfigurationSetting record, FactoryPurgeDeleteEffect effect,
        AuditDeleteState state, string detail, CancellationToken token = default)
    {
        var closed = effect with { State = state, UpdatedAt = clock.GetUtcNow(), Detail = detail };
        await SetExactAsync(owner, record, JsonSerializer.Serialize(closed, FactoryGitHubJson.Options), token);
        return (state, detail);
    }

    private static async Task<string?> InspectCascadeAsync(
        FactoryPreparationArm arm, FactoryPurgeResource resource,
        FactoryPurgePlan plan, CancellationToken token)
    {
        if (!IsCascadeParent(resource.ResourceType)) return null;
        var group = ResourceGroup(resource.ResourceId);
        if (group is null) return "Transitive deletion scope cannot be proven; preserved " + resource.ResourceId + ".";
        try
        {
            using var response = await arm.SendAsync(HttpMethod.Get, group + "/resources", "2021-04-01", token);
            if (response.StatusCode != HttpStatusCode.OK)
                return "Current transitive child inventory unavailable; preserved " + resource.ResourceId + ".";
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var descendants = document.RootElement.GetProperty("value").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString() ?? throw new JsonException())
                .Where(id => id.StartsWith(resource.ResourceId + "/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (descendants.Length != 0)
                return "Parent deletion blocked by current child " + descendants[0]
                    + "; preserved parent " + resource.ResourceId + ".";
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            return "Current transitive deletion impact is unknown; preserved " + resource.ResourceId + ".";
        }
    }

    private static async Task<Inspection> InspectAsync(
        FactoryPreparationArm arm, FactoryPurgeResource resource, CancellationToken token)
    {
        try
        {
            using var response = await arm.SendAsync(HttpMethod.Get, resource.ResourceId, resource.ApiVersion, token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(false, true, "Owned resource already absent: " + resource.ResourceId);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, false, "Current resource inspection denied; preserved: " + resource.ResourceId);
            if (response.StatusCode != HttpStatusCode.OK)
                return new(false, false, "Current resource identity is unknown; preserved: " + resource.ResourceId);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (!Same(root.GetProperty("id").GetString(), resource.ResourceId))
                return new(false, false, "Resource ID changed or was recreated; preserved: " + resource.ResourceId);
            if (!MatchesIncarnation(root, resource.Incarnation,
                    resource.ResourceType.Equals("Microsoft.AppConfiguration/configurationStores",
                        StringComparison.OrdinalIgnoreCase)))
                return new(false, false, "Resource incarnation changed or was human-modified; preserved: "
                    + resource.ResourceId + "; current=" + CurrentIncarnation(root));
            return new(true, false, "");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(false, false, "Current resource ownership is unknown; preserved: " + resource.ResourceId);
        }
    }

    private static bool MatchesIncarnation(JsonElement resource, string incarnation, bool allowExpectedContentMutation)
    {
        if (incarnation.StartsWith("etag-sha256:", StringComparison.Ordinal)
            && resource.TryGetProperty("etag", out var etag))
            return incarnation == "etag-sha256:" + FactoryReservationRecords.Hash(etag.GetString()!);
        if (resource.TryGetProperty("systemData", out var systemData))
        {
            var created = systemData.GetProperty("createdAt").GetDateTimeOffset().ToUniversalTime();
            if (!allowExpectedContentMutation && systemData.TryGetProperty("lastModifiedAt", out var modified)
                && modified.GetDateTimeOffset().ToUniversalTime() != created) return false;
            if (DateTimeOffset.TryParse(incarnation, out var expected))
                return (expected.ToUniversalTime() - created).Duration() < TimeSpan.FromSeconds(1);
            if (incarnation.StartsWith("appinsights-app-id:", StringComparison.Ordinal)
                && incarnation.LastIndexOf(";created:", StringComparison.Ordinal) is var marker && marker >= 0
                && DateTimeOffset.TryParse(incarnation[(marker + 9)..], out expected))
                return (expected.ToUniversalTime() - created).Duration() < TimeSpan.FromSeconds(1);
        }
        if (allowExpectedContentMutation
            && resource.TryGetProperty("properties", out var appConfiguration)
            && appConfiguration.TryGetProperty("creationDate", out var creationDate)
            && DateTimeOffset.TryParse(incarnation, out var expectedStoreCreation))
            return (expectedStoreCreation.ToUniversalTime()
                    - creationDate.GetDateTimeOffset().ToUniversalTime()).Duration() < TimeSpan.FromSeconds(1);
        if (incarnation.StartsWith("appinsights-app-id:", StringComparison.Ordinal)
            && resource.TryGetProperty("properties", out var properties)
            && properties.TryGetProperty("AppId", out var appId)
            && properties.TryGetProperty("CreationDate", out var creation)
            && incarnation.LastIndexOf(";created:", StringComparison.Ordinal) is var appMarker && appMarker >= 0
            && incarnation[("appinsights-app-id:").Length..appMarker] == appId.GetString()
            && DateTimeOffset.TryParse(incarnation[(appMarker + 9)..], out var expectedCreation))
            return (expectedCreation.ToUniversalTime() - creation.GetDateTimeOffset().ToUniversalTime()).Duration()
                < TimeSpan.FromSeconds(1);
        return false;
    }

    private static string CurrentIncarnation(JsonElement resource) =>
        resource.TryGetProperty("systemData", out var systemData)
            && systemData.TryGetProperty("createdAt", out var created)
            ? created.GetRawText()
            : resource.TryGetProperty("etag", out var etag) ? "etag:" + etag.GetString()
            : resource.TryGetProperty("properties", out var properties)
              && properties.TryGetProperty("CreationDate", out var date) ? "created:" + date.GetRawText()
              : "unsupported:" + string.Join(",", resource.EnumerateObject().Select(property => property.Name))
                + (resource.TryGetProperty("properties", out var currentProperties)
                    ? "(" + string.Join(",", currentProperties.EnumerateObject().Select(property => property.Name)) + ")"
                    : "");

    private OwnerReservationStore Store(string endpoint, FactoryReservationIdentity identity) =>
        new(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);

    private static string Revision(IEnumerable<ConfigurationSetting> records)
    {
        var value = string.Join("\n", records.OrderBy(record => record.Key, StringComparer.Ordinal)
            .Select(record => record.Key + "\0" + record.ETag));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ResourceType(string id)
    {
        var marker = id.LastIndexOf("/providers/", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? "Microsoft.Resources/resourceGroups" : id[(marker + 11)..].Split('/')[0]
            + "/" + id[(marker + 11)..].Split('/')[1];
    }

    private static string? Parent(string id)
    {
        var slash = id.LastIndexOf('/');
        return slash <= 0 ? null : id[..slash];
    }

    private static string? ResourceGroup(string id)
    {
        var marker = id.IndexOf("/providers/", StringComparison.OrdinalIgnoreCase);
        return marker <= 0 ? null : id[..marker];
    }

    private static bool IsCascadeParent(string resourceType) =>
        resourceType.Equals("Microsoft.App/managedEnvironments", StringComparison.OrdinalIgnoreCase)
        || resourceType.Equals("Microsoft.Network/virtualNetworks", StringComparison.OrdinalIgnoreCase)
        || resourceType.Equals("Microsoft.DocumentDB/databaseAccounts", StringComparison.OrdinalIgnoreCase)
        || resourceType.Equals("Microsoft.KeyVault/vaults", StringComparison.OrdinalIgnoreCase);

    private static bool IsProductConfiguration(FactoryPurgeResource resource) =>
        resource.ResourceType.Equals("Microsoft.AppConfiguration/configurationStores",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAuditBacking(string resourceId, AuditGraph? graph)
    {
        if (graph?.Records.Any(record => Same(record.Identity.NativeResourceId, resourceId)) == true)
            return true;
        var type = ResourceType(resourceId);
        return type.Equals("Microsoft.AppConfiguration/configurationStores", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Microsoft.DocumentDB/databaseAccounts", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> PendingEffects(
        IReadOnlyDictionary<string, ConfigurationSetting> authority)
    {
        var pending = new List<string>();
        foreach (var record in authority.Values)
        {
            try
            {
                if (record.Key == FactoryPreviewPreparation.RootKey)
                {
                    var coordinator = JsonSerializer.Deserialize<PreviewPreparationCoordinator>(
                        record.Value, FactoryGitHubJson.Options);
                    if (coordinator?.State is PreviewPreparationState.Reviewed
                        or PreviewPreparationState.Partial or PreviewPreparationState.Unknown)
                        pending.Add("Unresolved preview preparation coordinator remains.");
                    continue;
                }
                if (record.Key == FactoryLive.RootKey)
                {
                    var coordinator = JsonSerializer.Deserialize<FactoryLiveCoordinator>(
                        record.Value, FactoryGitHubJson.Options);
                    if (coordinator?.State is FactoryLiveState.Reviewed
                        or FactoryLiveState.Preparing or FactoryLiveState.Unknown)
                        pending.Add("Unresolved live preparation coordinator remains.");
                    continue;
                }
                if (record.Key == FactoryPublisher.RootKey)
                {
                    var coordinator = JsonSerializer.Deserialize<FactoryPublisherCoordinator>(
                        record.Value, FactoryGitHubJson.Options);
                    if (coordinator?.State is FactoryPublisherState.Reviewed
                        or FactoryPublisherState.Preparing or FactoryPublisherState.Unknown)
                        pending.Add("Unresolved publisher preparation coordinator remains.");
                    continue;
                }
                if (record.Key.Contains("/effects/", StringComparison.Ordinal))
                {
                    var effect = JsonSerializer.Deserialize<PreviewPreparationEffect>(
                        record.Value, FactoryGitHubJson.Options);
                    if (effect?.State is PreviewPreparationEffectState.Intended
                        or PreviewPreparationEffectState.Dispatched or PreviewPreparationEffectState.Unknown)
                        pending.Add("Unresolved preparation effect remains at " + effect.Target + ".");
                    continue;
                }
                var foundation = JsonSerializer.Deserialize<FoundationOperation>(
                    record.Value, FactoryGitHubJson.Options);
                if (foundation is { State: FoundationOperationState.Intent or FoundationOperationState.InFlight }
                    || foundation is { Disposition: FoundationResourceDisposition.CreateRequested })
                    pending.Add("Unresolved foundation effect remains at " + foundation.Target + ".");
            }
            catch (JsonException)
            {
                if (record.Key.Contains("/effects/", StringComparison.Ordinal))
                    pending.Add("Unreadable effect record blocks purge: " + record.Key + ".");
            }
        }
        return pending.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.TrimEnd('/'), right?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownAuthorityKinds = new(StringComparer.Ordinal)
    {
        "decide-foundation-operation",
        "decide-github-operation",
        "decide-model-operation",
        "decide-offboarding-request",
        "decide-pause-request",
        "decide-preview-capability",
        "decide-preview-operation",
        "decide-source-operation",
        "decide-supplemental-operation",
        "decide-telemetry-operation",
    };

    private static FactoryPurgeResult Result(
        FactoryPurgeState state, string detail, FactoryPurgePlan plan,
        FactoryPurgeRequest? request, bool retired) =>
        new(state, detail)
        {
            Plan = plan,
            Request = request,
            IdentityRetired = retired,
            Preserved = plan.PreservedAssets,
            Blocked = plan.BlockedDeletions,
            Residual = plan.ResidualEffects,
        };

    private sealed record AuditDisposition(string Description, IReadOnlyList<string> Blockers);
    private sealed record Inspection(bool Eligible, bool Absent, string Detail);
    private sealed record PurgePlanEnvelope(int Protocol, string Kind, string Data);
}

internal sealed record FactoryPurgeAuditDispositionResult(bool Eligible, string Detail);

internal static class FactoryPurgeAuditDisposition
{
    internal static FactoryPurgeAuditDispositionResult Evaluate(
        AuditGraph graph, AuditRetentionApproval policy, DateTimeOffset now)
    {
        if (policy.Protocol != 1 || policy.FactoryId != graph.FactoryId
            || policy.Policy != graph.Policy || policy.Days != 90
            || policy.ApprovedAt.Offset != TimeSpan.Zero)
            return new(false, "Approved expiry policy is invalid or belongs to different audit roots.");
        if (policy.ApprovedAt.AddDays(policy.Days) > now)
            return new(false, "Approved audit expiry is unreached; evidence remains retained.");
        if (graph.Records.Any(record => record.Status == AuditRetentionStatus.Protected))
            return new(false,
                "Protected or referenced evidence remains; ordinary 90-day policy never authorizes its disposal.");
        if (graph.Records.Count == 0
            || graph.Records.Any(record => record.Status != AuditRetentionStatus.Eligible))
            return new(false,
                "Ordinary 90-day policy alone cannot dispose retained or younger evidence; exact expiry effects must complete first.");
        return new(true, "Reached approved retention expiry for fully eligible disposed records.");
    }
}
