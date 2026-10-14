using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryReservationService(
    RepositoryAttachmentReviewer? reviewer, IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    public async Task<FactoryReservationResult> ReserveAsync(
        ApplicationBoundarySelection selection, string fingerprint, CancellationToken token)
    {
        var plan = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure)).PlanAsync(selection, token, fingerprint);
        if (plan.State != BoundaryPlanState.Reviewed)
            return new(Map(plan.State), "Only a complete CURRENT reviewed plan can reserve. No admission dispatched.") { Plan = plan };
        var createdAt = clock.GetUtcNow();
        var bytes = PlanBytes(plan, createdAt);
        if (bytes.Length > FactoryReservationRecords.MaxPlanBytes || plan.OwnerAdmission!.ProposedClaims.Count > FactoryReservationRecords.MaxClaims)
            return new(FactoryReservationState.Failed, "Reviewed plan exceeds reservation limits (1 MiB / 512 claims); no admission dispatched.");
        var identity = new FactoryReservationIdentity(Guid.NewGuid(), Guid.NewGuid(), plan.Selection.OwnerAppConfigEndpoint,
            plan.Selection.ProductKey, plan.RepositoryReview.Repository!.Id, plan.Selection.TenantId, plan.Selection.SubscriptionId,
            plan.Selection.ApplicationEnvironment, plan.SelectionFingerprint, plan.Fingerprint,
            plan.Azure!.OperatorPrincipalId!, plan.RepositoryReview.Operator!.Id, createdAt);
        var anchor = new FactoryReservationAnchor(1, FactoryReservationRecords.AnchorKind, identity,
            FactoryReservationRecords.Hash(bytes), bytes.Length,
            (bytes.Length + FactoryReservationRecords.ChunkBytes - 1) / FactoryReservationRecords.ChunkBytes,
            plan.OwnerAdmission.ProposedClaims.Count);
        var steps = FactoryReservationRecords.Steps(anchor, bytes, plan);
        var store = Store(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId, identity.AzureActorId);
        var key = ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey);
        var value = FactoryReservationRecords.Serialize(anchor);
        ConfigurationSetting root;
        try { root = await store.WriteAsync(key, value, null, token); }
        catch (ReservationStoreException e)
        {
            return new(token.IsCancellationRequested ? FactoryReservationState.Canceled : e.State,
                "Initial conditional admission was not confirmed. This first record is the bootstrap anchor; "
                + "no earlier durable intent is claimed. " + e.Message, identity)
            { HasRegistryEffects = e.State == FactoryReservationState.Unknown };
        }
        return (await ContinueAsync(store, root, anchor, steps, token)) with { HasRegistryEffects = true };
    }

    public Task<FactoryReservationResult> StatusAsync(
        string endpoint, string tenant, string subscription, string product, CancellationToken token) =>
        RecoverAsync(endpoint, tenant, subscription, product, false, null, token);

    public Task<FactoryReservationResult> ResumeAsync(
        string endpoint, string tenant, string subscription, string product, ApplicationBoundarySelection? repairInput, CancellationToken token) =>
        RecoverAsync(endpoint, tenant, subscription, product, true, repairInput, token);

    private async Task<FactoryReservationResult> RecoverAsync(
        string endpoint, string tenant, string subscription, string product, bool resume, ApplicationBoundarySelection? repairInput, CancellationToken token)
    {
        var store = Store(endpoint, tenant, subscription);
        var root = await store.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(product), token);
        if (root is null) return new(FactoryReservationState.Unknown, "No durable admission is visible; resume cannot invent an earlier write, identity or approval.");
        if (TryReadRetirement(root, product, out _))
            return new(FactoryReservationState.Blocked,
                "Factory identity is permanently retired; status, resume and reattachment are forbidden. Onboard only a new identity after retained retirement review.");
        var anchor = ReadAnchor(root, endpoint, tenant, subscription, product);
        try
        {
            if (anchor.Foundation is null)
                await FactoryFoundationService.RequireNoOrphanedHistoryAsync(store, anchor.Identity.FactoryId, token);
            var proof = new ReservationAdmissionProof();
            proof.Add(root);
            using var content = new MemoryStream();
            var presentChunks = new Dictionary<int, byte[]>();
            for (var i = 0; i < anchor.PlanChunks; i++)
            {
                var record = await store.ReadAsync(ProductConfigurationKeys.OwnerReservationPlanChunk(anchor.Identity.FactoryId, i), token);
                if (record is null)
                {
                    if (i < anchor.CompletedSteps) throw new JsonException();
                    continue;
                }
                var chunk = FactoryReservationRecords.Parse<FactoryReservationPlanChunk>(record.Value);
                if (chunk.SchemaVersion != 1 || chunk.Kind != FactoryReservationRecords.ChunkKind
                    || chunk.Identity != anchor.Identity || chunk.Index != i) throw new JsonException();
                var bytes = Convert.FromBase64String(chunk.Data);
                if (bytes.Length != Math.Min(FactoryReservationRecords.ChunkBytes, anchor.PlanBytes - i * FactoryReservationRecords.ChunkBytes))
                    throw new JsonException();
                content.Write(bytes);
                presentChunks.Add(i, bytes);
                proof.Add(record);
            }
            var planBytes = content.ToArray();
            if (presentChunks.Count != anchor.PlanChunks)
            {
                if (!resume || repairInput is null)
                    return Result(anchor, root, resume ? FactoryReservationState.Blocked
                            : anchor.Intent is null ? anchor.State : FactoryReservationState.Unknown,
                        "Immutable plan upload is incomplete; admission stays held. Resume --input may supply the exact original selection "
                        + "as repair data, never as authority. Missing approved bytes cannot be guessed.")
                        with
                    { ConfirmedPlanChunks = presentChunks.Count };
                var reconstructed = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
                    .PlanAsync(repairInput, token, anchor.Identity.PlanFingerprint, proof);
                if (reconstructed.State != BoundaryPlanState.Reviewed)
                    return Result(anchor, root, Map(reconstructed.State), "Repair inputs/current authority do not match the immutable admitted review; no write dispatched.");
                planBytes = PlanBytes(reconstructed, anchor.Identity.CreatedAt);
                if (planBytes.Length != anchor.PlanBytes || FactoryReservationRecords.Hash(planBytes) != anchor.PlanSha256)
                    throw new JsonException();
                foreach (var (index, bytes) in presentChunks)
                    if (!planBytes.AsSpan(index * FactoryReservationRecords.ChunkBytes, bytes.Length).SequenceEqual(bytes))
                        throw new JsonException();
            }
            if (planBytes.Length != anchor.PlanBytes || FactoryReservationRecords.Hash(planBytes) != anchor.PlanSha256)
                throw new JsonException();
            var plan = FactoryReservationRecords.Parse<ApplicationBoundaryPlan>(Encoding.UTF8.GetString(planBytes));
            if (plan.State != BoundaryPlanState.Reviewed || plan.Fingerprint != anchor.Identity.PlanFingerprint
                || plan.SelectionFingerprint != anchor.Identity.SelectionFingerprint
                || plan.OwnerAdmission?.ProposedClaims.Count != anchor.ClaimCount
                || plan.RepositoryReview.Repository?.Id != anchor.Identity.RepositoryId
                || plan.Selection.ProductKey != product || plan.Selection.OwnerAppConfigEndpoint != endpoint
                || plan.Selection.TenantId != tenant || plan.Selection.SubscriptionId != subscription)
                throw new JsonException();
            if (repairInput is not null && BoundaryPlanFingerprint.Hash(BoundarySelectionInput.Normalize(repairInput)) != BoundaryPlanFingerprint.Hash(plan.Selection))
                return Result(anchor, root, FactoryReservationState.Blocked, "Local repair selection differs from the immutable remote plan; no revision was overwritten.");
            var steps = FactoryReservationRecords.Steps(anchor, planBytes, plan);
            var confirmedClaims = 1;
            for (var i = anchor.PlanChunks; i < steps.Count; i++)
            {
                var existing = await store.ReadAsync(steps[i].Key, token);
                if (existing is not null)
                {
                    if (existing.Value != steps[i].Value)
                        return Result(anchor, root, FactoryReservationState.Blocked, "A competing or changed immutable claim remains held; no takeover or release.");
                    proof.Add(existing);
                    confirmedClaims++;
                }
                else if (i < anchor.CompletedSteps && anchor.Offboarding is null) throw new JsonException();
            }
            if (anchor.State == FactoryReservationState.Reserved
                && (anchor.CompletedSteps != steps.Count || anchor.Intent is not null
                    || anchor.Offboarding is null && confirmedClaims != anchor.ClaimCount))
                throw new JsonException();
            if (anchor.Foundation is not null)
            {
                var foundation = new FactoryFoundationService(azure, http, clock);
                var admission = Result(anchor, root, anchor.State, "Foundation has started.") with
                { Plan = plan, ConfirmedClaims = confirmedClaims, ConfirmedPlanChunks = anchor.PlanChunks };
                FactoryReservationResult result;
                if (!resume) result = await foundation.InspectAsync(admission, anchor.Foundation, token);
                else result = await foundation.ResumeAsync(admission, anchor.Foundation,
                    () => new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
                        .PlanAsync(plan.Selection, token, anchor.Identity.PlanFingerprint, proof), token);
                return await FactoryReconfigurationProjection.ApplyAsync(result, reviewer, azure, http, clock, token);
            }
            if (!resume)
                return Result(anchor, root, anchor.Intent is not null && anchor.State == FactoryReservationState.Partial ? FactoryReservationState.Unknown : anchor.State,
                    "Verified owner reservation only; not provisioned. Approval is not App consent, access, readiness or activation.")
                    with
                { Plan = plan, ConfirmedClaims = confirmedClaims, ConfirmedPlanChunks = anchor.PlanChunks };
            var current = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
                .PlanAsync(plan.Selection, token, anchor.Identity.PlanFingerprint, proof);
            if (current.State != BoundaryPlanState.Reviewed)
                return Result(anchor, root, Map(current.State), "Current inputs or operator authority no longer match the admitted review; no mutation dispatched.") with { Plan = current };
            if (anchor.State == FactoryReservationState.Reserved)
                return Result(anchor, root, FactoryReservationState.Reserved, "Existing reservation verified against current authority and the exact admitted plan. Not provisioned.") with { Plan = plan };
            return await ContinueAsync(Store(endpoint, tenant, subscription, current.Azure!.OperatorPrincipalId), root, anchor, steps, token);
        }
        catch (ReservationStoreException e)
        { return Result(anchor, root, e.State, e.Message); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(anchor, root, FactoryReservationState.Canceled, "Canceled during trusted-state inspection; no further dispatch. Existing intent remains held."); }
        catch (Exception e) when (e is JsonException or FormatException or NullReferenceException)
        { return Result(anchor, root, FactoryReservationState.Unknown, "Immutable remote plan/claim integrity could not be established; no mutation dispatched."); }
    }

    private static async Task<FactoryReservationResult> ContinueAsync(
        OwnerReservationStore store, ConfigurationSetting root, FactoryReservationAnchor anchor,
        IReadOnlyList<(string Key, string Value)> steps, CancellationToken token)
    {
        var hasEffects = false;
        try
        {
            for (var index = anchor.CompletedSteps; index < steps.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var step = steps[index];
                var intent = new ReservationWriteIntent(index, step.Key, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Hash(step.Value));
                if (anchor.Intent is not null && anchor.Intent != intent)
                    return Result(anchor, root, FactoryReservationState.Blocked, "Stored mutation intent differs from the immutable operation; no forced retry.") with { HasRegistryEffects = hasEffects };
                if (anchor.Intent is null || anchor.State != FactoryReservationState.Partial)
                {
                    anchor = anchor with { Intent = intent, State = FactoryReservationState.Partial };
                    hasEffects = true;
                    root = await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(anchor), root.ETag, token);
                }
                var existing = await store.ReadAsync(step.Key, token);
                if (existing is null)
                {
                    hasEffects = true;
                    await store.WriteAsync(step.Key, step.Value, null, token);
                }
                else if (existing.Value != step.Value)
                    return Result(anchor, root, FactoryReservationState.Blocked, "Competing immutable record; partial admission stays held by this operation.") with { HasRegistryEffects = hasEffects };
                anchor = anchor with { CompletedSteps = index + 1, Intent = null };
                hasEffects = true;
                root = await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(anchor), root.ETag, token);
            }
            anchor = anchor with { State = FactoryReservationState.Reserved };
            hasEffects = true;
            root = await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(anchor), root.ETag, token);
            return Result(anchor, root, FactoryReservationState.Reserved, "Identity and owner-scoped claims reserved. Not provisioned; no grants or repository changes.") with { HasRegistryEffects = true };
        }
        catch (ReservationStoreException e)
        {
            anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value);
            if (e.State == FactoryReservationState.Failed && !token.IsCancellationRequested)
            {
                try
                {
                    anchor = anchor with { State = FactoryReservationState.Failed };
                    hasEffects = true;
                    root = await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(anchor), root.ETag, token);
                }
                catch (ReservationStoreException outcomeError)
                {
                    return Result(anchor, root, FactoryReservationState.Unknown,
                        "Step rejection was known, but its durable outcome update was not confirmed. " + outcomeError.Message) with
                    { HasRegistryEffects = hasEffects };
                }
            }
            return Result(anchor, root, token.IsCancellationRequested ? FactoryReservationState.Canceled : e.State, e.Message) with { HasRegistryEffects = hasEffects };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(anchor, root, FactoryReservationState.Canceled, "Canceled; dispatch stopped. Existing partial admission remains held; inspect pending intent.") with { HasRegistryEffects = hasEffects }; }
    }

    private OwnerReservationStore Store(string endpoint, string tenant, string subscription, string? expectedActor = null) =>
        new(endpoint, tenant, subscription, azure, http, expectedActor);
    private static byte[] PlanBytes(ApplicationBoundaryPlan plan, DateTimeOffset admittedAt) =>
        Encoding.UTF8.GetBytes(BoundaryPlanFingerprint.Canonical(JsonSerializer.SerializeToElement(plan with
        {
            ObservedAt = admittedAt,
            RepositoryReview = plan.RepositoryReview with { ObservedAt = admittedAt },
        }, FactoryReservationRecords.Json)));
    private static FactoryReservationState Map(BoundaryPlanState state) => Enum.Parse<FactoryReservationState>(state.ToString());

    private static FactoryReservationAnchor ReadAnchor(ConfigurationSetting root, string endpoint, string tenant, string subscription, string product)
    {
        try
        {
            var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value);
            if (!FactoryReservationRecords.TryIdentify(root.Key, root.Label, root.Value, out _)
                || anchor.SchemaVersion != 1 || anchor.Kind != FactoryReservationRecords.AnchorKind
                || anchor.Identity.OwnerEndpoint != endpoint || anchor.Identity.ProductKey != product
                || anchor.Identity.TenantId != tenant || anchor.Identity.SubscriptionId != subscription)
                throw new JsonException();
            return anchor;
        }
        catch (Exception e) when (e is JsonException or NullReferenceException)
        { throw new ReservationStoreException(FactoryReservationState.Unknown, "Unrecognized or inconsistent remote admission; no adoption or overwrite."); }
    }

    private static bool TryReadRetirement(
        ConfigurationSetting root, string product, out FactoryRetirementPointer? retirement)
    {
        retirement = null;
        try
        {
            var value = FactoryReservationRecords.Parse<FactoryRetirementPointer>(root.Value);
            if (root.Key != ProductConfigurationKeys.OwnerProductClaim(product)
                || root.Label is not (null or "\0")
                || value.Protocol != 1 || value.Kind != FactoryPurge.RetirementKind
                || value.FactoryId == Guid.Empty || value.PurgeOperationId == Guid.Empty
                || value.ProductKey != product || value.PlanFingerprint.Length != 64
                || value.PlanFingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || string.IsNullOrWhiteSpace(value.AuditDisposition)
                || string.IsNullOrWhiteSpace(value.AuditRevision)
                || string.IsNullOrWhiteSpace(value.ApprovedBy)
                || value.RetiredAt == default || value.RetiredAt.Offset != TimeSpan.Zero)
                return false;
            retirement = value;
            return true;
        }
        catch (Exception e) when (e is JsonException or NullReferenceException)
        {
            return false;
        }
    }

    private static FactoryReservationResult Result(FactoryReservationAnchor anchor, ConfigurationSetting root, FactoryReservationState state, string detail) =>
        new(state, detail, anchor.Identity)
        {
            Revision = root.ETag.ToString(),
            PendingIntent = anchor.Intent,
            ConfirmedClaims = 1 + Math.Max(0, anchor.CompletedSteps - anchor.PlanChunks),
            ConfirmedPlanChunks = Math.Min(anchor.CompletedSteps, anchor.PlanChunks),
        };
}
