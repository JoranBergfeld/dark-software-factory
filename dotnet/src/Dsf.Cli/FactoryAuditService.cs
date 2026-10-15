using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryAuditService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryAuditResult> ExportAsync(FactoryReservationResult admission, AuditGraph graph, string path,
        string? approval, bool verify, CancellationToken token)
    {
        var identity = admission.Identity!;
        var store = new OwnerReservationStore(admission.Foundation!.ProductEndpoint!, identity.TenantId,
            identity.SubscriptionId, azure, http, identity.AzureActorId);
        var key = FactoryAudit.Namespace + "exports/" + FactoryAudit.TextHash(path);
        if (verify)
        {
            var record = await store.ReadAsync(key + "/receipt", token)
                ?? throw new FactoryAuditException(FactoryAuditState.Blocked, "Protected export receipt missing; export request or copied file is not proof.");
            var receipt = JsonSerializer.Deserialize<AuditExportReceipt>(record.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            var intentRecord = await store.ReadAsync(key + "/intent", token) ?? throw new JsonException();
            var original = JsonSerializer.Deserialize<AuditExportIntent>(intentRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (receipt.Protocol != 1 || receipt.Operation == Guid.Empty || receipt.VerifiedAt < original.ApprovedAt || receipt.VerifiedAt > clock.GetUtcNow()
                || original.Protocol != 1 || original.Operation != receipt.Operation || original.FactoryId != receipt.FactoryId
                || original.Path != receipt.Path || original.GraphRevision != receipt.GraphRevision || original.Policy != receipt.Policy
                || original.ApprovedBy != receipt.ApprovedBy || receipt.Records != graph.Records.Count) throw new JsonException();
            if (receipt.GraphRevision != graph.Revision || receipt.Policy != graph.Policy)
                throw new FactoryAuditException(FactoryAuditState.Blocked, "Export roots/revisions/retention context are obsolete; no disposition authority.");
            if (receipt.FactoryId != identity.FactoryId || receipt.Path != path
                || receipt.Policy != graph.Policy || receipt.ApprovedBy != identity.AzureActorId
                || receipt.ManifestSha256 != FactoryAuditExport.Verify(graph, path, token)) throw new JsonException();
            var fresh = await ReadAsync(admission, token);
            if (fresh.Revision != graph.Revision) throw new FactoryAuditException(FactoryAuditState.Blocked, "Roots changed while verifying export.");
            return new(FactoryAuditState.Verified, "Private artifact readable and reference-closed at exact current retained revisions; deletion remains separately governed.")
                { Revision = graph.Revision, Receipt = receipt };
        }
        path = FactoryAuditExport.ReviewPath(path);
        var fingerprint = FactoryModelsValidation.Hash(new { graph.FactoryId, GraphRevision = graph.Revision, graph.Policy, Path = path,
            ApprovedBy = identity.AzureActorId });
        if (approval is null) return new(FactoryAuditState.Reviewed, "Review exact private export destination and revision closure; no artifact or remote writes.")
            { Revision = graph.Revision, ApprovalFingerprint = fingerprint };
        if (approval != fingerprint) throw new FactoryAuditException(FactoryAuditState.Blocked, "Exact export review fingerprint required; no writes.");
        await store.RequirePristineAsync(key + "/", token);
        var intent = new AuditExportIntent(1, identity.FactoryId, Guid.NewGuid(), graph.Revision, graph.Policy, path,
            identity.AzureActorId, clock.GetUtcNow());
        var exports = await store.ReadNamespaceAsync(FactoryAudit.Namespace + "exports/", token, 32);
        if (exports.Count > 30) throw new FactoryAuditException(FactoryAuditState.Partial, "Bounded export history exhausted; retain existing receipts, no new operation.");
        await store.WriteAsync(key + "/intent", JsonSerializer.Serialize(intent, FactoryGitHubJson.Options), null, token);
        var hash = FactoryAuditExport.Write(graph, path, token);
        var current = await ReadAsync(admission, token);
        if (current.Revision != graph.Revision) throw new FactoryAuditException(FactoryAuditState.Unknown, "Export created but roots changed; no receipt. Retain original operation/artifact.");
        if (hash != FactoryAuditExport.Verify(current, path, token)) throw new JsonException();
        var result = new AuditExportReceipt(1, identity.FactoryId, intent.Operation, graph.Revision, graph.Policy, path,
            hash, graph.Records.Count, identity.AzureActorId, clock.GetUtcNow());
        await store.WriteAsync(key + "/receipt", JsonSerializer.Serialize(result, FactoryGitHubJson.Options), null, token);
        if ((await ReadAsync(admission, token)).Revision != graph.Revision || hash != FactoryAuditExport.Verify(graph, path, token))
            throw new FactoryAuditException(FactoryAuditState.Unknown, "Receipt recorded but final graph/artifact changed; inspect original evidence, no current receipt released.");
        return new(FactoryAuditState.Exported, "Private readable export verified against complete internal references; protected receipt recorded. No deletion or activation authorized.")
            { Revision = graph.Revision, Receipt = result };
    }

    internal async Task<AuditGraph> ReadAsync(FactoryReservationResult admission, CancellationToken token)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Identity is not { } identity
            || admission.Foundation?.ProductEndpoint is not { } endpoint)
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Reserved owner identity and protected product endpoint required.");
        var product = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        var analysis = new OwnerReservationStore(FactorySourceValidation.ProgressEndpoint(identity.FactoryId),
            identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        var governance = await product.ReadNamespaceAsync("dsf/factory/", token, FactoryAudit.MaximumRecords, FactoryAudit.MaximumBytes, 1024);
        var records = new List<FactoryAuditRecord>();
        Add(governance, AuditStoreKind.Governance);
        var captured = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        if (governance.ContainsKey(ProductConfigurationKeys.FactorySource))
        {
            foreach (var prefix in new[] { "source/", "intent/" })
            {
                var values = await analysis.ReadNamespaceAsync(prefix, token, FactorySourceValidation.MaximumRecords, FactorySourceValidation.MaximumMetadataBytes, 1024);
                Add(values, AuditStoreKind.Analysis);
                foreach (var value in values) captured.Add(value.Key, value.Value);
            }
        }
        await ReadPreviewAsync(admission, governance, records, token);
        await ReadActivityAsync(admission, governance, records, token);
        var graph = FactoryAudit.Build(identity.FactoryId, records, clock.GetUtcNow());
        _ = FactoryAuditDocuments.Read(graph);
        if (governance.ContainsKey(ProductConfigurationKeys.CharterAdopted))
            _ = await FactoryCharterAdoptionReader.ReadAsync(identity.FactoryId, product, token);
        foreach (var prefix in new[] { "source/", "intent/" })
            if (governance.ContainsKey(ProductConfigurationKeys.FactorySource))
                await FenceAsync(captured.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(), analysis, prefix, token);
        await FenceAsync(governance, product, "dsf/factory/", token);
        return graph;

        void Add(IReadOnlyDictionary<string, ConfigurationSetting> values, AuditStoreKind kind)
        {
            foreach (var (key, record) in values)
            {
                if (key.StartsWith(FactoryAudit.Namespace + "exports/", StringComparison.Ordinal)) continue;
                if (FactoryReattachment.IsStopRecord(key)
                    || key.StartsWith(FactoryReattachment.AccessPrefix, StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(record.Value, new JsonDocumentOptions { AllowDuplicateProperties = false });
                records.Add(new(new(identity.FactoryId, kind, key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value)),
                    record.LastModified ?? throw new JsonException(), record.Value, [], AuditRetentionStatus.Retained));
            }
        }
    }

    internal static FactoryAuditResult Inspect(AuditGraph graph, int page) => new(FactoryAuditState.Inspected,
        "Native revision-pinned retained evidence; historical context is not current source, model, readiness or activation authority.")
    {
        Revision = graph.Revision,
        ProtectedRecords = graph.Records.Count(r => r.Status == AuditRetentionStatus.Protected),
        RetainedRecords = graph.Records.Count(r => r.Status == AuditRetentionStatus.Retained),
        EligibleRecords = graph.Records.Count(r => r.Status == AuditRetentionStatus.Eligible),
        Records = graph.Records.Skip(page * FactoryAudit.PageSize).Take(FactoryAudit.PageSize).ToArray(),
        NextPage = (page + 1) * FactoryAudit.PageSize < graph.Records.Count ? page + 1 : null,
    };

    private static async Task FenceAsync(IReadOnlyDictionary<string, ConfigurationSetting> records, OwnerReservationStore store, string prefix, CancellationToken token)
    {
        var current = await store.ReadNamespaceAsync(prefix, token, FactoryAudit.MaximumRecords, FactoryAudit.MaximumBytes, 1024);
        if (current.Count != records.Count) throw new JsonException("Audit namespace changed during inspection.");
        foreach (var (key, expected) in records)
        {
            if (!current.TryGetValue(key, out var actual) || actual.ETag != expected.ETag || actual.Value != expected.Value
                || actual.LastModified != expected.LastModified || actual.ContentType != expected.ContentType)
                throw new JsonException("Audit roots changed during inspection.");
        }
    }
}
