using System.Net;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryPreviewMetadataProof(IReadOnlyDictionary<string, string> revisions)
{
    internal bool Includes(JsonElement assignment) =>
        revisions.TryGetValue(assignment.GetProperty("id").GetString()!, out var revision)
            && FactoryModelsValidation.Hash(assignment.GetProperty("properties")) == revision;

    internal static async Task<FactoryPreviewMetadataProof> ReadAsync(FactoryReservationResult admission, OwnerReservationStore store,
        FactoryPreparationArm arm, string principal, TimeProvider clock, CancellationToken token)
    {
        var records = await store.ReadNamespaceAsync("dsf/factory/preview", token);
        var manifestRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw Unconfirmed();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(manifestRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (records.Count == 0)
        {
            if (manifest.PreviewOperationId is not null || manifest.PreviewApprovalFingerprint is not null) throw Unconfirmed();
            return new(new Dictionary<string, string>());
        }
        if (records.Values.Any(r => r.ContentType != (FactoryPreviewPublication.IsPreparationRecord(r.Key)
            ? FactoryPreviewPublication.PreparationContentType : FactoryPreviewValidation.RecordContentType))) throw Unconfirmed();
        var historyManifest = manifest;
        var reconfiguration = await store.ReadAsync(FactoryReconfigurationOperationRecords.RootKey, token);
        if (reconfiguration is not null)
        {
            var transition = FactoryReconfigurationOperationRecords.Read(
                reconfiguration.Value, admission.Identity!, clock.GetUtcNow());
            if (transition.Phase is FactoryReconfigurationPhase.Reconciling or FactoryReconfigurationPhase.AwaitingEvidence)
                historyManifest = transition.ReviewedManifest;
        }
        DefinitePreviewPreparation history;
        try { history = FactoryPreviewPublication.ReadDefinite(historyManifest,
            records.ToDictionary(p => p.Key, p => p.Value.Value), clock.GetUtcNow()); }
        catch (PreviewCapabilityException) { throw Unconfirmed(); }
        if (history.Plan.Manifest.Identity != admission.Identity) throw Unconfirmed();
        var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in history.Plan.Steps.Where(s => history.Effects[s.Index].State == PreviewPreparationEffectState.Confirmed)
            .Select(s => s.Grant).OfType<PreviewPreparationGrant>().Where(g => g.PrincipalId == principal))
        {
            var receipt = history.Receipts.Single(r => r.Target == grant.Target);
            var role = await ReadAsync(grant.RoleDefinitionId);
            if (FactoryModelsValidation.Hash(role.GetProperty("properties")) != grant.RoleRevision
                || role.GetProperty("properties").GetProperty("permissions").GetArrayLength() != 1)
                throw Unconfirmed();
            var permission = role.GetProperty("properties").GetProperty("permissions")[0];
            if (FactoryModelsValidation.Strings(permission, "dataActions").Length != 0
                || FactoryModelsValidation.Strings(permission, "notDataActions").Length != 0
                || FactoryModelsValidation.Strings(permission, "notActions").Length != 0
                || !FactoryModelsValidation.Strings(permission, "actions").Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(grant.Actions.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw Unconfirmed();
            var assignment = await ReadAsync(grant.Target);
            var p = assignment.GetProperty("properties");
            if (FactoryModelsValidation.Hash(p) != receipt.NativeRevision || p.GetProperty("principalId").GetString() != principal
                || p.GetProperty("principalType").GetString() != "ServicePrincipal" || !Same(p.GetProperty("scope").GetString(), grant.Scope)
                || !Same(p.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
                || p.GetProperty("createdBy").GetString() != manifest.Identity.AzureActorId
                || p.GetProperty("createdOn").GetDateTimeOffset().ToString("O") != receipt.Incarnation
                || p.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
                || p.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0")
                throw Unconfirmed();
            revisions.Add(grant.Target, receipt.NativeRevision);
        }
        var current = await store.ReadNamespaceAsync("dsf/factory/preview", token);
        if (current.Count != records.Count || records.Any(p => !current.TryGetValue(p.Key, out var r) || r.Value != p.Value.Value || r.ETag != p.Value.ETag)
            || (await store.ReadAsync(manifestRecord.Key, token))?.ETag != manifestRecord.ETag) throw Unconfirmed();
        return new(revisions);

        async Task<JsonElement> ReadAsync(string target)
        {
            using var response = await arm.SendAsync(HttpMethod.Get, target, "2022-04-01", token);
            if (response.StatusCode != HttpStatusCode.OK) throw Unconfirmed();
            await response.Content.LoadIntoBufferAsync(FactoryPreviewPreparation.Policy.NativeResponseBytes, token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token), new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (!Same(document.RootElement.GetProperty("id").GetString(), target)) throw Unconfirmed();
            return document.RootElement.Clone();
        }
    }

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static ReservationStoreException Unconfirmed() => new(FactoryReservationState.Unknown,
        "CurrentReceiptBoundPreviewMetadataUnconfirmed; no grant adoption or source/model downgrade.");
}
