using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal sealed class AzureCharterEvidenceReader(
    string endpoint, TokenCredential credential, HttpClient http) : IFactoryCharterEvidenceReader
{
    public async Task<CharterEvidenceRecord?> ReadAsync(string key, CancellationToken token)
    {
        if (!key.StartsWith(FactoryCharterLimits.AuditPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Charter evidence reader is restricted to charter governance.");
        using var response = await GetAsync("/kv/" + Uri.EscapeDataString(key) + "?api-version=1.0", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        RequireReadable(response);
        try
        {
            using var document = await EvidencePayload.ReadAsync(response, token, 64 * 1024);
            var value = document.RootElement;
            var record = new CharterEvidenceRecord(value.GetProperty("key").GetString() ?? throw new JsonException(),
                value.GetProperty("label").GetString(), value.GetProperty("value").GetString() ?? throw new JsonException(),
                value.GetProperty("etag").GetString() ?? throw new JsonException(), value.GetProperty("content_type").GetString());
            if (record.Key != key || record.Label is not null || string.IsNullOrEmpty(record.ETag)
                || record.ContentType != FactoryCharterLimits.RecordContentType)
                throw new JsonException();
            return record;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new CharterAdoptionEvidenceException("Exact native charter key, label, revision and record type unconfirmed."); }
    }

    public async Task RequirePristineAsync(string prefix, CancellationToken token)
    {
        if (prefix != ProductConfigurationKeys.CharterAdoptionsPrefix)
            throw new InvalidOperationException("Only adoption history absence may be inspected.");
        using var response = await GetAsync("/kv?api-version=1.0&key=" + Uri.EscapeDataString(prefix + "*")
            + "&label=*&$select=key", token);
        RequireReadable(response);
        try
        {
            using var document = await EvidencePayload.ReadAsync(response, token);
            var value = document.RootElement;
            if (response.Headers.Contains("Link") || value.TryGetProperty("@nextLink", out _)
                || value.TryGetProperty("nextLink", out _)
                || value.GetProperty("items").ValueKind != JsonValueKind.Array
                || value.GetProperty("items").GetArrayLength() != 0)
                throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new CharterAdoptionEvidenceException("Adoption namespace absence unconfirmed; surviving history is not authority."); }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FactoryGovernance.NormalizeEndpoint(endpoint) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["https://appconfig.azure.com/.default"]), token)).Token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static void RequireReadable(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new CharterAdoptionEvidenceException("Current protected adoption evidence unavailable; no context released.");
    }
}
