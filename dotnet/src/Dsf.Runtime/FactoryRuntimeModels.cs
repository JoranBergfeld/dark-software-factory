using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>Completed promotion retains original model approval, never an evergreen capability observation.</summary>
internal static class FactoryRuntimeModels
{
    internal const string ContinuationProtocol = "promoted-model-observation-v1";

    internal static async Task<FactoryModels> ReadAsync(FactoryManifest manifest, IReadOnlyDictionary<string, string> values,
        ModelCapabilityReference reference, DateTimeOffset now, TokenCredential credential, HttpClient http, CancellationToken token)
    {
        FactoryPause.RequireAdmissionOpen(values.Keys);
        var retained = FactoryModelsValidation.ReadRetained(manifest, values, reference, now);
        if (retained.Proof!.ExpiresAt > now) return retained;
        var gateway = new AzureConfigurationSettingsGateway(credential, http);
        var records = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        await foreach (var record in gateway.ListRecordsAsync(retained.Configuration.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
        {
            FactoryPause.RequireAdmissionOpen([record.Key]);
            if (records.Count >= FactoryPreviewPublication.Policy.MaximumRecords + FactoryAudit.GovernanceHistoryRecords
                || Encoding.UTF8.GetByteCount(record.Key + record.Value) + 1152 > FactoryCharterLimits.RecordBytes
                || string.IsNullOrEmpty(record.ETag.ToString()) || !records.TryAdd(record.Key, record))
                throw new JsonException("Bounded original model governance required.");
        }
        foreach (var prefix in new[] { FactoryPause.Key, FactoryPause.HistoryPrefix, FactoryPromotion.Prefix })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var record in gateway.ListRecordsAsync(retained.Configuration.ProductEndpoint, "*", token, prefix + "*"))
            {
                FactoryPause.RequireAdmissionOpen([record.Key]);
                if (record.Label is not null || !seen.Add(record.Key) || !records.TryGetValue(record.Key, out var captured)
                    || record.Value != captured.Value || record.ETag != captured.ETag)
                    throw new JsonException("Original promotion namespace changed.");
            }
            if (!seen.SetEquals(records.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))))
                throw new JsonException("Complete promotion namespace required.");
        }
        var current = Read(manifest, records.ToDictionary(p => p.Key, p => p.Value.Value), reference, now, records);
        if (FactoryModelsValidation.Hash(current) != FactoryModelsValidation.Hash(retained))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ModelGovernanceChangedDuringObservation");
        return current;
    }

    internal static FactoryModels Read(FactoryManifest manifest, IReadOnlyDictionary<string, string> values,
        ModelCapabilityReference reference, DateTimeOffset now, IReadOnlyDictionary<string, ConfigurationSetting> records)
    {
        FactoryPause.RequireAdmissionOpen(values.Keys.Concat(records.Keys));
        var retained = FactoryModelsValidation.ReadRetained(manifest, values, reference, now);
        if (retained.Proof!.ExpiresAt > now) return retained;
        try
        {
            var nativeValues = records.ToDictionary(p => p.Key, p => p.Value.Value);
            var publisher = FactoryPublisher.ReadConfirmed(nativeValues.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal))
                .ToDictionary(), now);
            var identities = records.ToDictionary(p => p.Key, p => new AuditRecordIdentity(manifest.Identity.FactoryId,
                AuditStoreKind.Governance, p.Key, p.Value.ETag.ToString(), FactoryAudit.TextHash(p.Value.Value)));
            var promotion = FactoryPromotion.RequireApproved(nativeValues, identities, publisher.Plan, now);
            var currentManifest = FactoryGovernance.ReadInactiveManifest(nativeValues[ProductConfigurationKeys.FactoryManifest],
                nativeValues[ProductConfigurationKeys.FactoryProfile], manifest.Identity.ProductKey, retained.Configuration.ProductEndpoint, manifest.ProfilePin);
            var authorityRevision = FactoryModelsValidation.Hash(records.Values.Where(r => !r.Key.StartsWith(FactoryAudit.Namespace, StringComparison.Ordinal))
                .OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => identities[r.Key]).ToArray());
            if (FactoryModelsValidation.Hash(currentManifest) != FactoryModelsValidation.Hash(manifest)
                || publisher.Plan.Deployment.Manifest.Identity != manifest.Identity
                || publisher.Plan.Deployment.Manifest.ProfilePin != manifest.ProfilePin
                || authorityRevision != publisher.Plan.Activation.AuthorityRevision)
                throw new JsonException("Promoted model governance changed.");
            // First promotion must itself have used a then-current, complete native capability proof.
            var originallyVerified = FactoryModelsValidation.ReadVerified(manifest, nativeValues, reference, promotion.ApprovedAt);
            if (FactoryModelsValidation.Hash(originallyVerified) != FactoryModelsValidation.Hash(retained))
                throw new JsonException("Original model observation changed.");
            return retained;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "ExpiredModelObservationRequiresOriginalCompletedPromotion; retained proof is historical, not current health.");
        }
    }

    internal static void RequireProtocol(FactoryModels models, ModelAuthorityResult result, DateTimeOffset now)
    {
        if (models.Proof!.ExpiresAt <= now && result.ContinuationProtocol != ContinuationProtocol)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "CurrentModelObservationProtocolRequired; upgrade the reviewed host explicitly, no historical-health fallback.");
    }
}
