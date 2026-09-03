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
}
