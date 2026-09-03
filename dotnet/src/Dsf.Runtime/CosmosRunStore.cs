using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Upserts a document into a Cosmos DB container. The real implementation talks to
/// the account's data plane with the runtime's managed identity; tests substitute
/// a hand-written double rather than a live account.
/// </summary>
internal interface ICosmosDocumentGateway
{
    Task UpsertAsync(
        string endpoint,
        string database,
        string container,
        string partitionKey,
        string id,
        string json,
        CancellationToken cancellationToken);

    /// <summary>
    /// Point-reads the document at <paramref name="id"/>/<paramref name="partitionKey"/>,
    /// or returns <c>null</c> if no document has ever been written under that id.
    /// </summary>
    Task<string?> ReadAsync(
        string endpoint,
        string database,
        string container,
        string partitionKey,
        string id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically creates the document at <paramref name="id"/>/<paramref name="partitionKey"/>
    /// only if none exists yet, so two callers racing to write the same id can
    /// never both succeed: exactly one creates it, and every other caller
    /// (concurrent or later) is told <c>false</c> -- a document was already
    /// there -- instead of overwriting it. The default implementation composes
    /// the two other members (a non-atomic read-then-write) for gateways that
    /// have not opted into a real atomic primitive; <see cref="AzureCosmosDocumentGateway"/>
    /// overrides this with an actual unconditional Cosmos create that reports a
    /// document conflict rather than racing a read against a write.
    /// </summary>
    async Task<bool> CreateIfAbsentAsync(
        string endpoint,
        string database,
        string container,
        string partitionKey,
        string id,
        string json,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(endpoint, database, container, partitionKey, id, cancellationToken);
        if (existing is not null)
        {
            return false;
        }

        await UpsertAsync(endpoint, database, container, partitionKey, id, json, cancellationToken);
        return true;
    }
}

/// <summary>
/// The Cosmos data-plane gateway: an Entra-authenticated REST upsert, using the
/// same <see cref="DefaultAzureCredential"/> path as the rest of the runtime so a
/// Container App needs no keys and no interactive login.
/// </summary>
internal sealed class AzureCosmosDocumentGateway(TokenCredential? credential = null, HttpClient? httpClient = null)
    : ICosmosDocumentGateway
{
    private const string CosmosApiVersion = "2018-12-31";
    private static readonly string[] Scopes = ["https://cosmos.azure.com/.default"];

    private readonly TokenCredential credential = credential ?? new DefaultAzureCredential();
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task UpsertAsync(
        string endpoint,
        string database,
        string container,
        string partitionKey,
        string id,
        string json,
        CancellationToken cancellationToken)
    {
        var token = await this.credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var uri = new Uri(new Uri(EnsureTrailingSlash(endpoint)), $"dbs/{database}/colls/{container}/docs");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization", Uri.EscapeDataString($"type=aad&ver=1.0&sig={token.Token}"));
        request.Headers.TryAddWithoutValidation("x-ms-version", CosmosApiVersion);
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("r"));
        request.Headers.TryAddWithoutValidation("x-ms-documentdb-is-upsert", "true");
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKey }));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }

    public async Task<string?> ReadAsync(
        string endpoint,
        string database,
        string container,
        string partitionKey,
        string id,
        CancellationToken cancellationToken)
    {
        var token = await this.credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var uri = new Uri(new Uri(EnsureTrailingSlash(endpoint)), $"dbs/{database}/colls/{container}/docs/{id}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(
            "Authorization", Uri.EscapeDataString($"type=aad&ver=1.0&sig={token.Token}"));
        request.Headers.TryAddWithoutValidation("x-ms-version", CosmosApiVersion);
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("r"));
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKey }));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// The real atomic create: an unconditional Cosmos insert (no
    /// <c>x-ms-documentdb-is-upsert</c> header), so a document id that already
    /// exists is reported by Cosmos itself as a 409 Conflict -- an atomic
    /// data-plane guarantee, not a read-then-write race the gateway could lose.
    /// </summary>
    public async Task<bool> CreateIfAbsentAsync(
        string endpoint,
        string database,
        string container,
        string partitionKey,
        string id,
        string json,
        CancellationToken cancellationToken)
    {
        var token = await this.credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var uri = new Uri(new Uri(EnsureTrailingSlash(endpoint)), $"dbs/{database}/colls/{container}/docs");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization", Uri.EscapeDataString($"type=aad&ver=1.0&sig={token.Token}"));
        request.Headers.TryAddWithoutValidation("x-ms-version", CosmosApiVersion);
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("r"));
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKey }));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return true;
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

/// <summary>
/// The run blackboard, persisted in the product's Cosmos account: every station
/// checkpoint upserts the whole run document, partitioned by product and keyed by
/// run id, so the run's evidence, decisions, checkpoints and audit trail survive
/// the process and can be resumed and governed afterwards.
/// </summary>
internal sealed class CosmosRunStore(
    string endpoint,
    string database,
    string container,
    string product,
    ICosmosDocumentGateway gateway) : IRunStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var document = JsonSerializer.Serialize(
            new
            {
                id = run.Id,
                product,
                station,
                status = run.Status.ToString().ToLowerInvariant(),
                trigger = run.Trigger.ToString().ToLowerInvariant(),
                dryRun = run.DryRun,
                fingerprint = run.Fingerprint,
                productHints = run.ProductHints,
                sourceKinds = run.SourceKinds,
                checkpoints = run.Checkpoints,
                evidence = run.Evidence,
                proposals = run.Proposals.Select(proposal => new
                {
                    proposal.Id,
                    proposal.Title,
                    proposal.SourceKind,
                    proposal.IntentKey,
                    proposal.Confidence,
                    proposal.Accepted,
                    proposal.Labels,
                    proposal.EvidenceReferences,
                }),
                filedIssues = run.FiledIssues,
                previewedIssues = run.PreviewedIssues.Select(preview => new
                {
                    preview.Title,
                    preview.IntentKey,
                    preview.Labels,
                }),
                failureReason = run.FailureReason,
                audit = run.Audit.Select(record => new { record.Station, record.Message }),
                updatedAt = DateTimeOffset.UtcNow,
            },
            SerializerOptions);

        try
        {
            await gateway.UpsertAsync(endpoint, database, container, product, run.Id, document, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not persist run '{run.Id}' at station '{station}' to the Cosmos container "
                + $"'{database}/{container}' at '{endpoint}': {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Reads the run document back by id, so a resumed run finds and continues
    /// what a prior process already persisted -- checkpoints, evidence,
    /// proposals, audit trail and terminal status all intact -- instead of
    /// starting over blind to its own history. Returns <c>null</c> when no
    /// document has ever been written under <paramref name="runId"/>.
    /// </summary>
    public async Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runId);

        string? json;
        try
        {
            json = await gateway.ReadAsync(endpoint, database, container, product, runId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not read run '{runId}' from the Cosmos container "
                + $"'{database}/{container}' at '{endpoint}': {exception.Message}",
                exception);
        }

        return json is null ? null : Deserialize(json);
    }

    /// <summary>Rebuilds a <see cref="ConveyorRun"/> from the document <see cref="SaveAsync"/> wrote.</summary>
    private static ConveyorRun Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var run = new ConveyorRun
        {
            Id = root.GetProperty("id").GetString()!,
            Trigger = Enum.Parse<TriggerKind>(root.GetProperty("trigger").GetString()!, ignoreCase: true),
            ProductHints = ReadStrings(root, "productHints"),
            SourceKinds = ReadStrings(root, "sourceKinds"),
            DryRun = root.GetProperty("dryRun").GetBoolean(),
        };
        run.Status = Enum.Parse<RunStatus>(root.GetProperty("status").GetString()!, ignoreCase: true);
        run.Fingerprint = root.TryGetProperty("fingerprint", out var fingerprint)
            ? fingerprint.GetString() ?? string.Empty
            : string.Empty;
        run.FailureReason = root.TryGetProperty("failureReason", out var failureReason)
            && failureReason.ValueKind != JsonValueKind.Null
                ? failureReason.GetString()
                : null;

        if (root.TryGetProperty("checkpoints", out var checkpoints))
        {
            run.Checkpoints.AddRange(ReadStrings(checkpoints));
        }

        if (root.TryGetProperty("evidence", out var evidence))
        {
            foreach (var item in evidence.EnumerateArray())
            {
                run.Evidence.Add(new EvidenceItem(
                    item.GetProperty("sourceKind").GetString()!,
                    item.GetProperty("reference").GetString()!,
                    item.GetProperty("summary").GetString()!));
            }
        }

        if (root.TryGetProperty("proposals", out var proposals))
        {
            foreach (var item in proposals.EnumerateArray())
            {
                var proposal = new Proposal(
                    item.GetProperty("id").GetString()!,
                    item.GetProperty("title").GetString()!,
                    item.GetProperty("sourceKind").GetString()!,
                    ReadStrings(item, "evidenceReferences"))
                {
                    IntentKey = item.GetProperty("intentKey").GetString() ?? string.Empty,
                    Confidence = item.GetProperty("confidence").GetDouble(),
                    Accepted = item.GetProperty("accepted").GetBoolean(),
                };
                proposal.Labels.AddRange(ReadStrings(item, "labels"));
                run.Proposals.Add(proposal);
            }
        }

        if (root.TryGetProperty("filedIssues", out var filedIssues))
        {
            run.FiledIssues.AddRange(ReadStrings(filedIssues));
        }

        if (root.TryGetProperty("previewedIssues", out var previewedIssues))
        {
            foreach (var item in previewedIssues.EnumerateArray())
            {
                run.PreviewedIssues.Add(new IssuePreview(
                    item.GetProperty("title").GetString()!,
                    item.GetProperty("intentKey").GetString()!,
                    ReadStrings(item, "labels")));
            }
        }

        if (root.TryGetProperty("audit", out var audit))
        {
            foreach (var item in audit.EnumerateArray())
            {
                run.Audit.Add(new AuditRecord(
                    item.GetProperty("station").GetString()!,
                    item.GetProperty("message").GetString()!));
            }
        }

        return run;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var array) ? ReadStrings(array) : [];

    private static IReadOnlyList<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(element => element.GetString()!).ToArray();
}
