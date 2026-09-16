using System.Text;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public static class FactoryTelemetryLimits
{
    public const int DetailJsonBytes = 1024;
    public const int ItemIdCharacters = 128;
    public const int HostEndpointCharacters = 261;
    public const int ProviderOperationIdCharacters = 128;

    public static int StringJsonBytes(string value) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, FactoryGitHubJson.Options)) - 2;

    public static bool FitsDetail(string value) => value.Length <= DetailJsonBytes && StringJsonBytes(value) <= DetailJsonBytes;

    public static void ValidateProof(FactoryTelemetryConfiguration configuration, TelemetryProof proof)
    {
        var count = configuration.Backends.Sum(b => b.Tables.Count);
        if (!FitsDetail(proof.Detail) || proof.Records.Count > count || proof.Queries.Count > count
            || proof.Records.Select(r => (r.BackendResourceId, r.Table)).Distinct().Count() != proof.Records.Count
            || proof.Queries.Select(q => (q.BackendResourceId, q.Table)).Distinct().Count() != proof.Queries.Count
            || proof.AttributionBasis != FactoryTelemetryValidation.Attribution || proof.CoverageLimitations != FactoryTelemetryValidation.Coverage)
            throw new JsonException("Proof exceeds its reviewed lifecycle bounds; no truncation permitted.");
        foreach (var record in proof.Records) Reference(record);
        foreach (var query in proof.Queries)
        {
            var backend = Backend(query.BackendResourceId);
            if (!backend.Tables.Contains(query.Table) || !Enum.IsDefined(query.Reason)
                || query.Records is < 0 or >= 500000
                || query.QuerySha256 != FactoryTelemetryValidation.Hash(configuration.Queries[query.Table])
                || query.ProviderRequestId is not null && (!Guid.TryParse(query.ProviderRequestId, out var request)
                    || request.ToString() != query.ProviderRequestId))
                throw new JsonException("Query observation exceeds its reviewed bounds.");
            if (query.LatestRecord is { } latest)
            {
                Reference(latest);
                if (latest.BackendResourceId != query.BackendResourceId || latest.Table != query.Table) throw new JsonException();
            }
        }

        TelemetryBackend Backend(string id) =>
            configuration.Backends.SingleOrDefault(b => b.ResourceId == id) ?? throw new JsonException("Unreviewed reference.");
        void Reference(TelemetryRecordReference record)
        {
            var backend = Backend(record.BackendResourceId);
            if (record.WorkspaceId != backend.WorkspaceId || !backend.Tables.Contains(record.Table)
                || !backend.ApplicationResourceIds.Contains(record.ApplicationResourceId, StringComparer.Ordinal)
                || record.ObservedRecords is < 1 or >= 500000 || string.IsNullOrWhiteSpace(record.ItemId)
                || record.ItemId.Length > ItemIdCharacters || record.ItemId.Any(char.IsControl))
                throw new JsonException("Record reference exceeds its reviewed bounds.");
        }
    }
}
