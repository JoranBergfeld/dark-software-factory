using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryAuditService
{
    private static readonly HttpClient NativeHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    private async Task ReadPreviewAsync(FactoryReservationResult admission, IReadOnlyDictionary<string, ConfigurationSetting> governance,
        List<FactoryAuditRecord> records, CancellationToken token)
    {
        if (governance.Keys.Any(k => k.StartsWith("dsf/factory/preview", StringComparison.Ordinal)))
        {
            if (!governance.ContainsKey(FactoryPreviewPreparation.RootKey)) throw new JsonException("Preview coordinator missing despite surviving history.");
            var values = governance.ToDictionary(p => p.Key, p => p.Value.Value);
            var manifest = JsonSerializer.Deserialize<FactoryManifest>(values[ProductConfigurationKeys.FactoryManifest], FactorySourceValidation.RecordJson) ?? throw new JsonException();
            var preparation = FactoryPreviewPublication.ReadConfirmed(manifest, values, clock.GetUtcNow());
            if (governance.ContainsKey(FactoryPreviewPublication.RootKey))
                _ = FactoryPreviewPublication.ValidatePublished(preparation, FactoryModelsValidation.ReadPreparation(manifest, values),
                    FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values), values, clock.GetUtcNow());
        }
        foreach (var configuration in governance.Values.Where(r => r.Key.StartsWith("dsf/factory/preview-execution-configuration/", StringComparison.Ordinal)))
        {
            var identity = admission.Identity!;
            var operation = Guid.Parse(configuration.Key.Split('/')[^1]);
            var execution = JsonSerializer.Deserialize<PreviewExecutionConfiguration>(configuration.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (execution.Seed.Id != FactoryPreviewExecution.Id(identity.FactoryId, operation)
                || execution.Policy != FactoryPreviewExecution.Policy) throw new JsonException();
            var preview = await NativeAsync(FactoryPreviewValidation.PreviewContainer, execution.Seed.Id, AuditStoreKind.Preview);
            if (preview.Identity.NativeResourceId != execution.Seed.ResourceId) throw new JsonException();
            var document = JsonNode.Parse(preview.Content)!.AsObject();
            if ((int?)document["protocol"] != 1 || (string?)document["kind"] != "council-preview-evaluation"
                || (string?)document["profile"] != "OnboardingPreview" || (Guid?)document["factoryId"] != identity.FactoryId
                || (Guid?)document["operationId"] != operation || (string?)document["id"] != execution.Seed.Id) throw new JsonException();
            using var nativeDocument = JsonDocument.Parse(preview.Content);
            var retained = PreviewEvaluationDocument.ReadNative(nativeDocument.RootElement);
            if (retained.CalculateChecksum() != retained.Checksum || retained.Initial().CalculateChecksum() != execution.Seed.Checksum)
                throw new JsonException("Preview checksum/initial seed mismatch.");
            if (retained.PendingRun is not null)
                throw new FactoryAuditException(FactoryAuditState.Unknown, "Native run checkpoint intent remains unconfirmed; no closure receipt.");
            var receipt = document["run"];
            if (receipt is not null)
            {
                var id = (string)receipt["id"]!;
                if (id != execution.Seed.Id + "-run") throw new JsonException();
                var run = await NativeAsync("runs", id, AuditStoreKind.Run);
                var body = JsonNode.Parse(run.Content)!;
                using var nativeRun = JsonDocument.Parse(run.Content);
                if (run.Identity.NativeResourceId != (string?)receipt["resourceId"] || run.Identity.NativeRevision != (string?)receipt["eTag"]
                    || (string?)body["onboardingChecksum"] != (string?)receipt["checksum"]
                    || retained.Run?.NativeDocumentSha256 != FactoryModelsValidation.Hash(nativeRun.RootElement)
                    || (int?)body["onboardingProtocol"] != 1 || (string?)body["profile"] != "OnboardingPreview"
                    || (bool?)body["dryRun"] != true || body["filedIssues"]!.AsArray().Count != 0)
                    throw new JsonException("Referenced native run differs.");
                records.Add(run);
            }
            records.Add(preview);
            var current = await NativeAsync(FactoryPreviewValidation.PreviewContainer, execution.Seed.Id, AuditStoreKind.Preview);
            if (current.Identity != preview.Identity) throw new JsonException("Native preview changed during audit.");

            async Task<FactoryAuditRecord> NativeAsync(string container, string id, AuditStoreKind kind)
            {
                var path = $"dbs/{Uri.EscapeDataString(identity.ProductKey)}/colls/{container}/docs/{Uri.EscapeDataString(id)}";
                var endpoint = "https://" + FactoryPreviewValidation.Prefix(identity.FactoryId) + "-cosmos.documents.azure.com/";
                var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
                    identity.AzureActorId, "https://cosmos.azure.com");
                var access = await credential.GetTokenAsync(new TokenRequestContext(["https://cosmos.azure.com/.default"]), token);
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + path);
                request.Headers.TryAddWithoutValidation("Authorization", Uri.EscapeDataString("type=aad&ver=1.0&sig=" + access.Token));
                request.Headers.TryAddWithoutValidation("x-ms-version", "2018-12-31");
                request.Headers.TryAddWithoutValidation("x-ms-date", clock.GetUtcNow().ToString("r"));
                request.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { identity.ProductKey }));
                using var response = await (http ?? NativeHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new FactoryAuditException(FactoryAuditState.Blocked, "Operator Cosmos point-read permission required on retained preview/runs containers; runtime permissions are unchanged.");
                if (response.StatusCode != HttpStatusCode.OK) throw new JsonException("Required native evidence unavailable.");
                await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token);
                var json = await response.Content.ReadAsStringAsync(token);
                using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
                var native = parsed.RootElement;
                var etag = native.GetProperty("_etag").GetString();
                if (etag is not { Length: >= 3 and <= 256 } || etag[0] != '"' || etag[^1] != '"'
                    || !response.Headers.TryGetValues("ETag", out var values) || !values.SequenceEqual([etag])
                    || native.GetProperty("id").GetString() != id || native.GetProperty("product").GetString() != identity.ProductKey)
                    throw new JsonException();
                var rid = NativeCosmosResourceId.RequireDocument(native.GetProperty("_rid").GetString());
                return new(new(identity.FactoryId, kind, path, etag, FactoryAudit.TextHash(json)) { NativeResourceId = rid },
                    kind == AuditStoreKind.Preview ? native.GetProperty("createdAt").GetDateTimeOffset() : identity.CreatedAt,
                    json, [], AuditRetentionStatus.Protected);
            }
        }
        if (governance.ContainsKey(FactoryPreviewPublication.RootKey)
            && !governance.Keys.Any(k => k.StartsWith("dsf/factory/preview-execution-configuration/", StringComparison.Ordinal)))
            throw new FactoryAuditException(FactoryAuditState.Partial, "Preview seed/publication has not reached a protected native execution binding; no complete closure receipt.");
    }
}
