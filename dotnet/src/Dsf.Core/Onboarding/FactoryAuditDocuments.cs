using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record AuditReadableDocument(string Kind, AuditRecordIdentity Owner,
    IReadOnlyList<AuditRecordIdentity> Sources, JsonElement Content);

public static class FactoryAuditDocuments
{
    public static IReadOnlyList<AuditReadableDocument> Read(AuditGraph graph)
    {
        var records = graph.Records.Where(r => r.Identity.Store == AuditStoreKind.Governance)
            .ToDictionary(r => r.Identity.Key, StringComparer.Ordinal);
        var documents = new List<AuditReadableDocument>();
        var manifestRecord = records[ProductConfigurationKeys.FactoryManifest];
        var manifest = Parse<FactoryManifest>(manifestRecord);
        if (manifest.PlanBytes is < 1 or > 1024 * 1024 || manifest.PlanChunks != (manifest.PlanBytes + 3999) / 4000)
            throw new JsonException("Invalid retained boundary plan budget.");
        var sources = new List<AuditRecordIdentity>();
        using (var bytes = new MemoryStream(manifest.PlanBytes))
        {
            for (var i = 0; i < manifest.PlanChunks; i++)
            {
                var record = records[ProductConfigurationKeys.FactoryPlanChunk(i)];
                var chunk = Parse<FactoryReservationPlanChunk>(record);
                var content = Convert.FromBase64String(chunk.Data);
                if (chunk.SchemaVersion != 1 || chunk.Kind != "decide-plan-chunk" || chunk.Identity != manifest.Identity
                    || chunk.Index != i || content.Length != Math.Min(4000, manifest.PlanBytes - i * 4000)) throw new JsonException();
                bytes.Write(content);
                sources.Add(record.Identity);
            }
            if (Hash(bytes.ToArray()) != manifest.ReviewedPlanSha256) throw new JsonException("Retained boundary plan checksum differs.");
            Add("ReviewedBoundary", manifestRecord, sources, JsonSerializer.Deserialize<ApplicationBoundaryPlan>(bytes.ToArray(),
                FactorySourceValidation.RecordJson) ?? throw new JsonException());
        }
        foreach (var record in records.Values.Where(r => r.Identity.Key.StartsWith(ProductConfigurationKeys.CharterAdoptionsPrefix, StringComparison.Ordinal)
            && r.Identity.Key.EndsWith("/receipt", StringComparison.Ordinal)).OrderBy(r => r.Identity.Key, StringComparer.Ordinal))
        {
            var adoption = Parse<CharterAdoption>(record);
            Payload("AdoptedCharter", record, adoption.PayloadRecords, adoption.PayloadSha256,
                i => ProductConfigurationKeys.CharterAdoptionPayload(adoption.Revision, i));
        }
        var published = new HashSet<Guid>();
        foreach (var record in records.Values.Where(r => r.Identity.Key == "dsf/factory/charter/publication"
            || r.Identity.Key.StartsWith("dsf/factory/charter/publications/history/", StringComparison.Ordinal))
            .OrderBy(r => r.Identity.Key, StringComparer.Ordinal))
        {
            var publication = Parse<CharterPublication>(record);
            if (published.Add(publication.Operation))
                Payload("ProposedCharter", record, publication.PayloadRecords, publication.PayloadSha256,
                    i => $"dsf/factory/charter/publications/{publication.Operation:N}/payload/{i:D4}");
        }
        if (ReadPreviewPreparation(records) is { } prepared)
        {
            Add("PreparedPreview", prepared.Owner, prepared.Chunks.Select(r => r.Identity).ToArray(), prepared.Plan);
        }
        return documents;

        void Payload(string kind, FactoryAuditRecord owner, int count, string hash, Func<int, string> key)
        {
            if (count is < 1 or > FactoryCharterLimits.MaximumPayloadRecords) throw new JsonException();
            var chunks = Enumerable.Range(0, count).Select(i => records[key(i)]).ToArray();
            var content = string.Concat(chunks.Select(Parse<string>));
            if (FactoryModelsValidation.Hash(content) != hash) throw new JsonException();
            Add(kind, owner, chunks.Select(r => r.Identity).ToArray(),
                JsonSerializer.Deserialize<CharterPublicationPayload>(content, FactorySourceValidation.RecordJson) ?? throw new JsonException());
        }
        void Add<T>(string kind, FactoryAuditRecord owner, IReadOnlyList<AuditRecordIdentity> references, T content)
        {
            var document = JsonSerializer.SerializeToElement(content, FactoryPreviewPublication.Json);
            if (FactorySourceEvidenceIntegrity.Sensitive(document.GetRawText()))
                throw new FactoryAuditException(FactoryAuditState.Blocked, "Sensitive decoded audit payload withheld; reconcile original redaction.");
            documents.Add(new(kind, owner.Identity, references, document));
        }
    }

    internal static (FactoryAuditRecord Owner, PreviewPreparationPlan Plan, IReadOnlyList<FactoryAuditRecord> Chunks)?
        ReadPreviewPreparation(IReadOnlyDictionary<string, FactoryAuditRecord> records)
    {
        if (!records.TryGetValue(FactoryPreviewPreparation.RootKey, out var preparation)) return null;
        var coordinator = Parse<PreviewPreparationCoordinator>(preparation);
        if (coordinator.PlanBytes is < 1 || coordinator.PlanBytes > FactoryPreviewPreparation.Policy.PlanBytes
            || coordinator.PlanChunks is < 1 || coordinator.PlanChunks > FactoryPreviewPreparation.Policy.MaximumChunks) throw new JsonException();
        var chunks = new List<FactoryAuditRecord>();
        using var bytes = new MemoryStream(coordinator.PlanBytes);
        for (var i = 0; i < coordinator.PlanChunks; i++)
        {
            var record = records[FactoryPreviewPreparation.ChunkKey(coordinator.OperationId, i)];
            var chunk = Convert.FromBase64String(Parse<string>(record));
            if (bytes.Length + chunk.Length > coordinator.PlanBytes) throw new JsonException();
            bytes.Write(chunk);
            chunks.Add(record);
        }
        if (bytes.Length != coordinator.PlanBytes || Hash(bytes.ToArray()) != coordinator.PlanSha256) throw new JsonException();
        var plan = JsonSerializer.Deserialize<PreviewPreparationPlan>(bytes.ToArray(), FactoryPreviewPublication.Json) ?? throw new JsonException();
        if (plan.Fingerprint != coordinator.PlanFingerprint) throw new JsonException();
        return (preparation, plan, chunks);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    private static T Parse<T>(FactoryAuditRecord record) =>
        JsonSerializer.Deserialize<T>(record.Content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
}
