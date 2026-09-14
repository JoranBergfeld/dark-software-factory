using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Runtime;

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
    private const string EndpointSetting = RuntimeSettingsComposer.AzureCosmosEndpoint;

    /// <summary>
    /// Cosmos DB's fixed data-plane AAD resource id (see Microsoft Learn: "Configure role-based
    /// access control for your Azure Cosmos DB account"). Every Cosmos account shares this
    /// resource id; it is not derived from the account's own hostname.
    /// </summary>
    private const string CosmosDataPlaneScope = "https://cosmos.azure.com/.default";

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
    private readonly string? ownerEndpoint;
    private readonly IOwnerRuntimeIndexReader? ownerRuntimeIndexReader;

    internal CosmosCharterStore(
        HttpClient httpClient,
        IAzureCliRunner runner,
        string? endpoint,
        string? database,
        string? ownerEndpoint = null,
        IOwnerRuntimeIndexReader? ownerRuntimeIndexReader = null)
    {
        this.httpClient = httpClient;
        this.runner = runner;
        this.endpoint = endpoint;
        this.database = database;
        this.ownerEndpoint = ownerEndpoint;
        this.ownerRuntimeIndexReader = ownerRuntimeIndexReader;
    }

    /// <summary>
    /// Resolves the product's Cosmos endpoint and database from the owner runtime index,
    /// with explicit environment overrides. Lookup is deferred until a charter operation.
    /// </summary>
    internal static CosmosCharterStore FromEnvironment() =>
        FromEnvironment(new HttpClient(), new SystemAzureCliRunner());

    internal static CosmosCharterStore FromEnvironment(HttpClient httpClient, IAzureCliRunner runner) => new(
        httpClient,
        runner,
        Environment.GetEnvironmentVariable(EndpointSetting),
        Environment.GetEnvironmentVariable(RuntimeIntegrationSettings.CosmosDatabase),
        Environment.GetEnvironmentVariable(RuntimeSettingsComposer.OwnerAppConfigEndpoint),
        new AzureCliAppConfigurationClient(runner));

    public async Task<StoredCharter?> GetCharterAsync(string product, CancellationToken cancellationToken)
    {
        var (account, db) = await ResolveConfigurationAsync(product, cancellationToken);
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
        var (account, db) = await ResolveConfigurationAsync(stored.Product, cancellationToken);
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

    private async Task<(Uri Account, string Database)> ResolveConfigurationAsync(
        string product,
        CancellationToken cancellationToken)
    {
        var resolvedEndpoint = endpoint?.Trim();
        var resolvedDatabase = database?.Trim();
        if (!string.IsNullOrWhiteSpace(ownerEndpoint))
        {
            var reader = ownerRuntimeIndexReader
                ?? throw new InvalidOperationException("An owner runtime index reader is required for charter configuration.");
            var values = await reader.ReadAsync(ownerEndpoint.Trim(), product, cancellationToken);
            if (string.IsNullOrWhiteSpace(resolvedEndpoint))
            {
                resolvedEndpoint = values.GetValueOrDefault(EndpointSetting)?.Trim();
            }
            if (string.IsNullOrWhiteSpace(resolvedDatabase))
            {
                resolvedDatabase = values.GetValueOrDefault(RuntimeIntegrationSettings.CosmosDatabase)?.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedEndpoint))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(ownerEndpoint)
                    ? $"{EndpointSetting} is required to read or write the stored charter; "
                        + $"set {RuntimeSettingsComposer.OwnerAppConfigEndpoint} to discover it from the owner runtime index."
                    : $"Product '{product}' has no {EndpointSetting} in the owner App Configuration runtime index "
                        + $"at '{ownerEndpoint}'; verify that dsf new completed runtime index publication.");
        }

        var db = string.IsNullOrWhiteSpace(resolvedDatabase) ? product : resolvedDatabase;
        return (new Uri(resolvedEndpoint.EndsWith('/') ? resolvedEndpoint : resolvedEndpoint + "/"), db);
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
        var scope = CosmosDataPlaneScope;
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
