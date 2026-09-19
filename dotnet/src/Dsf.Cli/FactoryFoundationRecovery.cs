using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryFoundationService
{
    private const string GroupRecoveryHandoff = "The original creation receipt is retained, but the Resource Groups GET contract supplies no "
        + "supported current resource-group incarnation. Recovery cannot prove the group was not recreated. "
        + "Blocked: hand off the original factory/operation and receipts to the resource administrator; no new ARM work, "
        + "name/tag adoption, replacement identity or cleanup.";

    internal static async Task RequireNoOrphanedHistoryAsync(
        OwnerReservationStore store, Guid factoryId, CancellationToken token)
    {
        var history = await store.ReadNamespaceAsync("dsf/foundation/", token);
        foreach (var record in history.Values)
        {
            var operation = FactoryReservationRecords.Parse<FoundationOperation>(record.Value);
            if (operation.SchemaVersion != 1 || operation.Kind != OperationKind || operation.FactoryId == Guid.Empty
                || operation.OperationId == Guid.Empty || operation.Index is < 0 or >= 1024
                || record.Key != ProductConfigurationKeys.FoundationOperation(operation.OperationId, operation.Index))
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Unrecognized owner foundation history cannot establish pristine state; no new foundation operation.");
            if (operation.FactoryId == factoryId)
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Foundation coordinator is missing but original operation history survives. Retain this factory ID and claims; administrator reconciliation is required, not fresh provisioning.");
        }
    }

    public async Task<FactoryReservationResult> ResumeAsync(
        FactoryReservationResult admission, FactoryFoundationPointer pointer, Func<Task<ApplicationBoundaryPlan>> review, CancellationToken token)
    {
        var inspected = await InspectAsync(admission, pointer, token);
        if (pointer.RetryAdmission is { } admitted
            && (admitted.Index < 0 || admitted.ClientRequestId == Guid.Empty || inspected.Foundation?.Operations.Count <= admitted.Index
                || inspected.Foundation?.Operations[admitted.Index].ClientRequestId != admitted.ClientRequestId))
            return RecoveryFailure(inspected, FactoryReservationState.Unknown,
                "Admitted retry has no matching new attempt receipt; the previous NoEffect result cannot fence a delayed actor. No takeover or dispatch; administrator handoff required.");
        if (pointer.Stage == "resource-group" && inspected.Plan is null) return inspected;
        var retry = pointer.Stage == "resource-group" && inspected.Foundation?.Operations is [{ State: FoundationOperationState.Failed,
                Disposition: FoundationResourceDisposition.NoEffect, HttpStatus: 400 or 401 or 403 } rejected]
            ? rejected : null;
        if (pointer is { OperationCount: 0, Stage: "resource-group" } || retry is not null)
        {
            var current = await review();
            if (current.State != BoundaryPlanState.Reviewed)
                return RecoveryFailure(inspected, Enum.Parse<FactoryReservationState>(current.State.ToString()),
                    "Current authority or reviewed inputs changed; no recovery dispatch.");
            return await ProvisionAsync(admission with { Foundation = inspected.Foundation }, true, token, retry);
        }
        if (pointer.Stage is "resource-group" or "product-configuration"
            && inspected.Foundation?.Operations is [{ State: FoundationOperationState.Completed }])
            return await RetainGroupHandoffAsync(admission, pointer, inspected, token);
        if (pointer.Stage is "product-configuration" or "product-authority-pending" or "product-authoritative")
            return await RecoverPublicationAsync(admission, pointer, inspected, token);
        if (pointer.Stage == "base-deployment" && inspected.Plan is not null)
            return await RecoverDeploymentAsync(admission, pointer, inspected, token);
        return inspected;
    }

    private async Task<FactoryReservationResult> RetainGroupHandoffAsync(
        FactoryReservationResult admission, FactoryFoundationPointer pointer, FactoryReservationResult inspected, CancellationToken token)
    {
        try
        {
            var store = Store(admission.Identity!);
            var (root, anchor, operations) = await ReadRecoveryJournalAsync(admission, pointer, inspected, store, token);
            var assets = new FactoryFoundationAssets(admission.Identity!, admission.Plan!.Selection);
            var original = operations.Single().Operation;
            if (original.Target != assets.Group || original.ApiVersion != "2021-04-01"
                || original.RequestSha256 != FactoryReservationRecords.Hash(assets.GroupRequest())
                || original.HttpStatus != 201 || original.ProviderRequestId is null || original.ResponseDate is null)
                throw new JsonException();
            if (pointer.State != FactoryFoundationState.Blocked || pointer.RecoveryDetail != GroupRecoveryHandoff)
                await TakeRecoveryCoordinatorAsync(store, root, anchor with { Foundation = pointer with
                    { State = FactoryFoundationState.Blocked, RecoveryDetail = GroupRecoveryHandoff } }, token);
            return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Blocked, GroupRecoveryHandoff);
        }
        catch (ReservationStoreException e)
        { return RecoveryFailureWithProgress(admission, inspected, e.State, e.Message); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Canceled, "Canceled; the original operation remains held. No cleanup."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Unknown, "Original group receipt or coordinator revision is unproven; no takeover or dispatch."); }
    }

    private async Task<FactoryReservationResult> RecoverDeploymentAsync(
        FactoryReservationResult admission, FactoryFoundationPointer pointer, FactoryReservationResult inspected, CancellationToken token)
    {
        var identity = admission.Identity!;
        var store = Store(identity);
        FactoryReservationAnchor? anchor = null;
        try
        {
            var journal = await ReadRecoveryJournalAsync(admission, pointer, inspected, store, token);
            var root = journal.Root;
            anchor = journal.Anchor;
            var operations = journal.Operations;
            var assets = new FactoryFoundationAssets(identity, admission.Plan!.Selection);
            VerifyInitialRequestRevision(operations.Select(o => o.Operation).ToArray(), assets);
            var requestHash = FactoryReservationRecords.Hash(assets.Request());
            var deployment = operations.SingleOrDefault(o => o.Operation.Target == assets.Deployment(pointer.OperationId));
            var parent = deployment.Operation;
            // Only an acknowledged native request can prove a delayed actor already sent its sole PUT.
            if (parent is null || parent.State is FoundationOperationState.Intent
                || parent.HttpStatus != 201 || parent.ProviderRequestId is null || parent.ResponseDate is null
                || parent.ProviderCorrelationId is null)
                return inspected with { Detail = "Original deployment dispatch is unresolved; a delayed actor may still send it. No takeover, retry or cleanup; administrator handoff required." };
            if (parent.RequestSha256 != requestHash || parent.ApiVersion != "2025-04-01")
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Installed deployment assets differ from the original request revision; no request reconstruction or takeover.");
            var targets = assets.Targets();
            var children = operations.Where(o => o.Operation.DeploymentTarget is not null).ToArray();
            if (children.Length != targets.Count || children.Select(o => o.Operation.Target).Distinct().Count() != targets.Count)
                throw new JsonException();
            foreach (var (operation, _) in children)
                if (!targets.Any(t => t.Id == operation.Target && t.ApiVersion == operation.ApiVersion
                        && t.LogicalIdentity == operation.LogicalIdentity && t.ManagedBy == operation.ManagedBy)
                    || operation.DeploymentTarget != parent.Target || operation.ClientRequestId != parent.ClientRequestId
                    || operation.RequestSha256 != requestHash)
                    throw new JsonException();
            var access = await new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
                identity.AzureActorId, "https://management.azure.com/")
                .GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
            await VerifyPublishedAuthorityAsync(admission, anchor, operations, access.Token, token);
            using var response = await ReadArmAsync(parent.Target, parent.ApiVersion, access.Token, token);
            if (!response.IsSuccessStatusCode) throw new JsonException();
            using var native = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var reconciled = ReadCreationReceipt(parent, native.RootElement, HttpStatusCode.Created);
            if (reconciled.State is not (FoundationOperationState.Completed or FoundationOperationState.Failed or FoundationOperationState.Canceled))
                return inspected with { Detail = "Original native deployment is not terminal; effects may continue. No takeover, redeployment or cleanup." };
            RequireUnchangedReceipt(parent, reconciled);

            Dictionary<int, FoundationOperation> verified;
            try
            {
                confirmedOperations[parent.Index] = reconciled;
                await ReconcileDeploymentAsync(store, root, anchor, assets, reconciled, children, access.Token, token, verifyOnly: true);
                verified = new(confirmedOperations);
            }
            finally
            {
                confirmedOperations.Clear();
                foreach (var (operation, _) in operations) confirmedOperations[operation.Index] = operation;
            }
            // Every next dispatch in the old coordinator requires its old root ETag; the acknowledged
            // single PUT cannot recur. This CAS fences future DSF dispatch, not ARM or administrators.
            (root, anchor) = await TakeRecoveryCoordinatorAsync(store, root, anchor, token);
            await SaveReconciledReceiptAsync(store, deployment.Record, parent, reconciled, token);
            confirmedOperations[parent.Index] = reconciled;
            foreach (var (operation, record) in children)
            {
                await SaveReconciledReceiptAsync(store, record, operation, verified[operation.Index], token);
                confirmedOperations[operation.Index] = verified[operation.Index];
            }
            (root, anchor) = await CompleteDeploymentAsync(store, root, anchor, token);
            return await InspectAsync(admission, anchor.Foundation!, token);
        }
        catch (ReservationStoreException e)
        { return RecoveryFailureWithProgress(admission, inspected, e.State, e.Message); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Canceled, "Canceled during recovery; prior native work may continue. No rollback or cleanup."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
            or HttpRequestException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException)
        { return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Unknown, "Original native operation/ownership or authoritative revision is unproven; no dispatch. Retain receipts for administrator reconciliation."); }
    }

    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor)> TakeRecoveryCoordinatorAsync(
        OwnerReservationStore store, ConfigurationSetting root, FactoryReservationAnchor anchor, CancellationToken token)
    {
        // Change the value, not merely re-PUT identical bytes and assume a provider advances its ETag.
        anchor = anchor with { Foundation = anchor.Foundation! with { CoordinatorId = Guid.NewGuid() } };
        return (await SaveRootAsync(store, root, anchor, token), anchor);
    }

    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor,
        List<(FoundationOperation Operation, ConfigurationSetting Record)> Operations)> ReadRecoveryJournalAsync(
        FactoryReservationResult admission, FactoryFoundationPointer pointer, FactoryReservationResult inspected,
        OwnerReservationStore store, CancellationToken token)
    {
        var root = await store.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(admission.Identity!.ProductKey), token)
            ?? throw new JsonException();
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value);
        if (root.ETag.ToString() != admission.Revision || anchor.Foundation != pointer || anchor.Identity != admission.Identity)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Owner coordinator revision changed; no takeover or dispatch.");
        var records = await store.ReadNamespaceAsync($"dsf/foundation/{pointer.OperationId:D}/operations/", token);
        if (records.Count != pointer.OperationCount || inspected.Foundation!.Operations.Count != records.Count)
            throw new JsonException();
        var operations = new List<(FoundationOperation Operation, ConfigurationSetting Record)>();
        for (var i = 0; i < pointer.OperationCount; i++)
        {
            var record = records[ProductConfigurationKeys.FoundationOperation(pointer.OperationId, i)];
            var operation = FactoryReservationRecords.Parse<FoundationOperation>(record.Value);
            var observed = inspected.Foundation.Operations[i];
            if (FactoryReservationRecords.Serialize(operation with { State = observed.State }) != FactoryReservationRecords.Serialize(observed))
                throw new JsonException();
            operations.Add((operation, record));
            confirmedOperations[i] = operation;
        }
        return (root, anchor, operations);
    }

    private async Task VerifyPublishedAuthorityAsync(FactoryReservationResult admission, FactoryReservationAnchor anchor,
        IReadOnlyList<(FoundationOperation Operation, ConfigurationSetting Record)> operations, string access, CancellationToken token)
    {
        var identity = admission.Identity!;
        var pointer = anchor.Foundation!;
        var configuration = operations.Single(o => o.Operation.Target.EndsWith("/configurationstores/dsfo-" + identity.FactoryId.ToString("N"), StringComparison.Ordinal)).Operation;
        if (configuration.State != FoundationOperationState.Completed || configuration.Incarnation is null
            || configuration.Endpoint != pointer.ProductEndpoint) throw new JsonException();
        using var current = await ReadArmAsync(configuration.Target, configuration.ApiVersion, access, token);
        if (!current.IsSuccessStatusCode) throw new JsonException();
        using var document = JsonDocument.Parse(await current.Content.ReadAsStringAsync(token));
        VerifyConfigurationPolicy(document.RootElement, admission.Plan!.Selection.FactoryLocation);
        if (ReadCreationReceipt(configuration, document.RootElement, HttpStatusCode.Created) != configuration)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Product App Configuration incarnation changed; no adoption of a recreated resource ID or product writes.");
        var product = new OwnerReservationStore(pointer.ProductEndpoint!, identity.TenantId, identity.SubscriptionId, azure, http);
        var publications = operations.Where(o => o.Operation.Target.StartsWith(pointer.ProductEndpoint + "/kv/", StringComparison.Ordinal)).ToArray();
        var profile = FactoryReservationRecords.Serialize(new FactoryOnboardingProfile(
            1, "onboard-decide-inactive-v1", identity.FactoryId, identity.PlanFingerprint, []));
        var expected = ExpectedPublications(admission, anchor, profile, FactoryReservationRecords.Hash(profile));
        if (publications.Length != expected.Count) throw new JsonException();
        for (var i = 0; i < publications.Length; i++)
        {
            var operation = publications[i].Operation;
            var (key, value) = expected[i];
            var record = await product.ReadAsync(key, token);
            if (operation.Target != pointer.ProductEndpoint + "/kv/" + Uri.EscapeDataString(key) || operation.ApiVersion != "1.0"
                || operation.State != FoundationOperationState.Completed || record is null || record.ETag.ToString() != operation.Incarnation
                || record.Value != value || FactoryReservationRecords.Hash(value) != operation.RequestSha256)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Product authority no longer has its original publication receipt; newer/recreated keys are never overwritten from the owner archive.");
        }
    }

    private static FactoryReservationResult RecoveryFailure(FactoryReservationResult inspected, FactoryReservationState state, string detail) =>
        inspected with
        {
            State = state, Detail = detail,
            Foundation = inspected.Foundation is null ? null : inspected.Foundation with
            { State = Enum.Parse<FactoryFoundationState>(state.ToString()), Detail = detail },
        };

    private FactoryReservationResult RecoveryFailureWithProgress(FactoryReservationResult admission, FactoryReservationResult inspected,
        FactoryReservationState state, string detail) => confirmedAnchor is null
        ? RecoveryFailure(inspected, state, detail) with { HasRegistryEffects = registryDispatchStarted }
        : FailureResult(admission, confirmedAnchor, state, detail);

    private FoundationOperation RetryOperation(FoundationOperation previous)
    {
        if (previous.State != FoundationOperationState.Failed || previous.Disposition != FoundationResourceDisposition.NoEffect
            || previous.ResourceId is not null || previous.Incarnation is not null || previous.ProviderCorrelationId is not null
            || previous.PreviousAttempts is { Count: >= 8 })
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Only a bounded, durably rejected no-effect attempt can retry; native failure/cancellation/uncertainty never qualifies.");
        return previous with
        {
            ClientRequestId = Guid.NewGuid(), StartedAt = clock.GetUtcNow(),
            State = FoundationOperationState.Intent, Disposition = FoundationResourceDisposition.CreateRequested,
            HttpStatus = null, ProviderRequestId = null, ResponseDate = null,
            PreviousAttempts = [.. previous.PreviousAttempts ?? [], new(previous.ClientRequestId, previous.StartedAt,
                previous.State, previous.Disposition, previous.HttpStatus, previous.ProviderRequestId, previous.ResponseDate)],
        };
    }
}
