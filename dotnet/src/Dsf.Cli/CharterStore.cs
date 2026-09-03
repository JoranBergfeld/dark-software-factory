using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dsf.Core.Charters;

namespace Dsf.Cli;

/// <summary>Persistence seam for the singleton-per-product <see cref="StoredCharter"/> record.</summary>
internal interface ICharterStore
{
    Task<StoredCharter?> GetCharterAsync(string product, CancellationToken cancellationToken);

    Task PutCharterAsync(StoredCharter stored, CancellationToken cancellationToken);
}

/// <summary>
/// Stores charters in the product's Cosmos DB account through the NoSQL data-plane REST
/// API, authenticated with the operator's Azure CLI login (Entra data-plane RBAC). One
/// document per product lives in the <c>charters</c> container, keyed on the product.
/// </summary>
internal sealed class CosmosCharterStore : ICharterStore
{
    private const string Container = "charters";
    private const string CosmosApiVersion = "2018-12-31";
    private const string EndpointSetting = "AZURE_COSMOS_ENDPOINT";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient;
    private readonly IAzureCliRunner runner;
    private readonly string? endpoint;
    private readonly string? database;

    internal CosmosCharterStore(HttpClient httpClient, IAzureCliRunner runner, string? endpoint, string? database)
    {
        this.httpClient = httpClient;
        this.runner = runner;
        this.endpoint = endpoint;
        this.database = database;
    }

    /// <summary>
    /// Binds to the Cosmos account named by <c>AZURE_COSMOS_ENDPOINT</c>, using the product
    /// database (<c>DSF_COSMOS_DATABASE</c>, defaulting to the product key). Configuration is
    /// resolved per call so an unrelated command never pays for — or fails on — charter config.
    /// </summary>
    internal static CosmosCharterStore FromEnvironment() => new(
        new HttpClient(),
        new SystemAzureCliRunner(),
        Environment.GetEnvironmentVariable(EndpointSetting),
        Environment.GetEnvironmentVariable("DSF_COSMOS_DATABASE"));

    public async Task<StoredCharter?> GetCharterAsync(string product, CancellationToken cancellationToken)
    {
        var (account, db) = RequireConfiguration(product);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(account, $"dbs/{db}/colls/{Container}/docs/{product}"));
        AddPartitionKey(request, product);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("stored", out var stored))
        {
            throw new InvalidOperationException(
                $"Cosmos charter document for product '{product}' has no 'stored' payload.");
        }

        return stored.Deserialize<StoredCharter>(SerializerOptions)
            ?? throw new InvalidOperationException(
                $"Cosmos charter document for product '{product}' could not be read.");
    }

    public async Task PutCharterAsync(StoredCharter stored, CancellationToken cancellationToken)
    {
        var (account, db) = RequireConfiguration(stored.Product);
        var payload = JsonSerializer.Serialize(
            new { id = stored.Product, product = stored.Product, stored },
            SerializerOptions);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(account, $"dbs/{db}/colls/{Container}/docs"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        AddPartitionKey(request, stored.Product);
        request.Headers.TryAddWithoutValidation("x-ms-documentdb-is-upsert", "true");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private (Uri Account, string Database) RequireConfiguration(string product)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                $"{EndpointSetting} is required to read or write the stored charter.");
        }

        var db = string.IsNullOrWhiteSpace(database) ? product : database;
        return (new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/"), db);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("x-ms-version", CosmosApiVersion);
        request.Headers.TryAddWithoutValidation(
            "x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant());
        var token = await AccessTokenAsync(request.RequestUri!, cancellationToken);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            Uri.EscapeDataString($"type=aad&ver=1.0&sig={token}"));
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<string> AccessTokenAsync(Uri account, CancellationToken cancellationToken)
    {
        var scope = $"{account.Scheme}://{account.Host}/.default";
        var result = await runner.RunAsync(
            ["account", "get-access-token", "--scope", scope, "-o", "json"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az account get-access-token --scope {scope} failed with exit code {result.ExitCode}: "
                + result.StandardError);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException(
                $"az account get-access-token --scope {scope} returned no access token.");
    }

    private static void AddPartitionKey(HttpRequestMessage request, string product) =>
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey",
            JsonSerializer.Serialize(new[] { product }));

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Cosmos {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} "
            + $"failed with {(int)response.StatusCode}: {detail}");
    }
}
