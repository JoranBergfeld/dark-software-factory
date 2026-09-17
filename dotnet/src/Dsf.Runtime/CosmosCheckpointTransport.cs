using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed record CosmosCheckpoint(string Json, string ETag);

internal enum CosmosCheckpointFailure { Conflict, Denied, Capacity, ExplicitWorkloadCredentialRequired, Unknown }

internal sealed class CosmosCheckpointException(CosmosCheckpointFailure failure)
    : InvalidOperationException("Native onboarding checkpoint " + failure + "; no overwrite, retry or remote hold inferred.")
{
    internal CosmosCheckpointFailure Failure { get; } = failure;
}

internal sealed partial class AzureCosmosDocumentGateway
{
    internal const int MaximumCheckpointBytes = 2 * 1024 * 1024 - 16 * 1024;
    private static readonly string[] SystemProperties = ["_rid", "_self", "_etag", "_attachments", "_ts"];

    public async Task<CosmosCheckpoint?> ReadCheckpointAsync(
        string endpoint, string database, string container, string partitionKey, string id, CancellationToken token)
    {
        using var request = await CheckpointRequestAsync(HttpMethod.Get, endpoint, database, container, partitionKey, id, token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        RequireStatus(response, HttpStatusCode.OK);
        return await CheckpointResponseAsync(response, token);
    }

    public async Task<CosmosCheckpoint> WriteCheckpointAsync(
        string endpoint, string database, string container, string partitionKey, string id, string json, string? etag, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumCheckpointBytes)
            throw new CosmosCheckpointException(CosmosCheckpointFailure.Capacity);
        using var request = await CheckpointRequestAsync(etag is null ? HttpMethod.Post : HttpMethod.Put,
            endpoint, database, container, partitionKey, etag is null ? null : id, token);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        RequireStatus(response, etag is null ? HttpStatusCode.Created : HttpStatusCode.OK);
        var result = await CheckpointResponseAsync(response, token);
        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(result.Json);
        if (expected.RootElement.EnumerateObject().Any(property =>
                !actual.RootElement.TryGetProperty(property.Name, out var value) || !JsonElement.DeepEquals(property.Value, value))
            || actual.RootElement.EnumerateObject().Any(property => !expected.RootElement.TryGetProperty(property.Name, out _)
                && !SystemProperties.Contains(property.Name, StringComparer.Ordinal)))
            throw new CosmosCheckpointException(CosmosCheckpointFailure.Unknown);
        return result;
    }

    private async Task<HttpRequestMessage> CheckpointRequestAsync(
        HttpMethod method, string endpoint, string database, string container, string partitionKey, string? id, CancellationToken token)
    {
        if (!explicitCredential) throw new CosmosCheckpointException(CosmosCheckpointFailure.ExplicitWorkloadCredentialRequired);
        var access = await credential.GetTokenAsync(new TokenRequestContext(Scopes), token);
        var path = $"dbs/{Uri.EscapeDataString(database)}/colls/{Uri.EscapeDataString(container)}/docs"
            + (id is null ? "" : "/" + Uri.EscapeDataString(id));
        var request = new HttpRequestMessage(method, new Uri(new Uri(EnsureTrailingSlash(endpoint)), path));
        request.Headers.TryAddWithoutValidation("Authorization", Uri.EscapeDataString($"type=aad&ver=1.0&sig={access.Token}"));
        request.Headers.TryAddWithoutValidation("x-ms-version", CosmosApiVersion);
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("r"));
        request.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKey }));
        return request;
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected) return;
        throw new CosmosCheckpointException(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
            ? CosmosCheckpointFailure.Conflict : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? CosmosCheckpointFailure.Denied : CosmosCheckpointFailure.Unknown);
    }

    private static async Task<CosmosCheckpoint> CheckpointResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var body = await EvidencePayload.ReadAsync(response, token, 2 * 1024 * 1024);
            var etag = body.RootElement.GetProperty("_etag").GetString();
            if (etag is not { Length: >= 3 and <= 256 } || etag[0] != '"' || etag[^1] != '"' || etag.Any(char.IsControl)
                || !response.Headers.TryGetValues("ETag", out var values) || !values.SequenceEqual([etag]))
                throw new JsonException();
            return new(body.RootElement.GetRawText(), etag);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or SupplementalSourceException or KeyNotFoundException)
        { throw new CosmosCheckpointException(CosmosCheckpointFailure.Unknown); }
    }
}
