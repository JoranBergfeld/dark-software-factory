using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class FactorySupplementalEvidence
{
    private async Task<(IReadOnlyList<SupplementalEvidenceItem>, string)> FoundryAsync(CancellationToken token)
    {
        var before = await KnowledgeAsync(token);
        var selection = configuration.Source;
        var endpoint = FactorySupplementalValidation.SearchEndpoint(selection);
        var retrieval = await new FoundryIqKnowledgeGateway(Credential(), http)
            .RetrieveAsync(endpoint, selection.KnowledgeBase!, selection.Query, token);
        if (retrieval.SourceActivities.Any(a => a.Type != "searchIndex"))
            throw new SupplementalSourceException(SupplementalReason.Unsupported);
        if (!retrieval.SourceActivities.Select(a => a.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .SequenceEqual(selection.KnowledgeSources.Select(s => s.Name).Order(StringComparer.Ordinal)))
            throw new SupplementalSourceException(SupplementalReason.Incomplete);
        if (retrieval.Results.Count > 16) throw new SupplementalSourceException(SupplementalReason.CapacityExceeded);
        var prefixes = selection.KnowledgeSources.Select(s => $"foundryiq:{endpoint}#{selection.KnowledgeBase}/{s.Name}/").ToArray();
        var items = retrieval.Results.Select(result =>
        {
            var prefix = prefixes.SingleOrDefault(p => result.Id.StartsWith(p, StringComparison.Ordinal));
            if (prefix is null || string.IsNullOrWhiteSpace(result.Content) || result.Content.Length > 8192 || result.Id.Length > 2048
                || SourceContentPolicy.Sensitive(result.Content))
                throw new SupplementalSourceException(SupplementalReason.MalformedResponse);
            return new SupplementalEvidenceItem(prefix + FactoryTelemetryValidation.Hash(result.Id[prefix.Length..]), result.Content);
        }).ToArray();
        if (await KnowledgeAsync(token) != before) throw new SupplementalSourceException(SupplementalReason.AuthorityChanged);
        return (items, before);
    }

    private async Task<string> KnowledgeAsync(CancellationToken token)
    {
        var selected = configuration.Source;
        using var bases = await KnowledgeDocumentAsync("knowledgebases", token);
        using var sources = await KnowledgeDocumentAsync("knowledgesources", token);
        if (!Names(bases.RootElement).SequenceEqual([selected.KnowledgeBase!])
            || !Names(sources.RootElement).Order(StringComparer.Ordinal).SequenceEqual(selected.KnowledgeSources.Select(s => s.Name).Order(StringComparer.Ordinal)))
            throw new SupplementalSourceException(SupplementalReason.AuthorityChanged);
        using var kb = await KnowledgeDocumentAsync("knowledgebases('" + selected.KnowledgeBase + "')", token);
        var native = kb.RootElement;
        if (native.GetProperty("name").GetString() != selected.KnowledgeBase
            || native.GetProperty("@odata.etag").GetString() != selected.KnowledgeBaseETag
            || native.GetProperty("outputMode").GetString() != "extractiveData"
            || !native.GetProperty("knowledgeSources").EnumerateArray().Select(s => s.GetProperty("name").GetString())
                .Order(StringComparer.Ordinal).SequenceEqual(selected.KnowledgeSources.Select(s => s.Name).Order(StringComparer.Ordinal)))
            throw new SupplementalSourceException(SupplementalReason.AuthorityChanged);
        var revisions = new List<string> { FactoryTelemetryValidation.Hash(native) };
        foreach (var source in selected.KnowledgeSources)
        {
            using var document = await KnowledgeDocumentAsync("knowledgesources('" + source.Name + "')", token);
            var value = document.RootElement;
            if (value.GetProperty("name").GetString() != source.Name || value.GetProperty("@odata.etag").GetString() != source.SourceETag
                || value.GetProperty("kind").GetString() != "searchIndex"
                || value.GetProperty("searchIndexParameters").GetProperty("searchIndexName").GetString() != source.IndexName)
                throw new SupplementalSourceException(SupplementalReason.Unsupported);
            revisions.Add(FactoryTelemetryValidation.Hash(value));
        }
        using var indexes = await KnowledgeDocumentAsync("indexes", token);
        var all = indexes.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if (!all.Select(i => i.GetProperty("name").GetString()).Order(StringComparer.Ordinal)
                .SequenceEqual(selected.KnowledgeSources.Select(s => s.IndexName).Order(StringComparer.Ordinal)))
            throw new SupplementalSourceException(SupplementalReason.AuthorityChanged);
        foreach (var index in all)
        {
            var selection = selected.KnowledgeSources.Single(s => s.IndexName == index.GetProperty("name").GetString());
            if (index.GetProperty("@odata.etag").GetString() != selection.IndexETag
                || index.TryGetProperty("permissionFilterOption", out var protection) && protection.ValueKind != JsonValueKind.Null
                || index.GetProperty("fields").EnumerateArray().Any(f => f.TryGetProperty("permissionFilter", out var p) && p.ValueKind != JsonValueKind.Null))
                throw new SupplementalSourceException(SupplementalReason.Unsupported);
            revisions.Add(FactoryTelemetryValidation.Hash(index));
        }
        return FactoryTelemetryValidation.Hash(revisions);
        static IEnumerable<string> Names(JsonElement collection) =>
            collection.GetProperty("value").EnumerateArray().Select(item => item.GetProperty("name").GetString() ?? throw new JsonException());
    }

    private async Task<JsonDocument> KnowledgeDocumentAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            FactorySupplementalValidation.SearchEndpoint(configuration.Source) + "/" + path + "?api-version=2026-04-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            (await Credential().GetTokenAsync(new TokenRequestContext(["https://search.azure.com/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new SupplementalSourceException(response.StatusCode == HttpStatusCode.NotFound ? SupplementalReason.MissingKnowledgeBase
                : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? SupplementalReason.PermissionDenied : SupplementalReason.NetworkUnavailable);
        var document = await EvidencePayload.ReadAsync(response, token);
        try { EvidencePayload.RequireComplete(document.RootElement); }
        catch (Exception e) when (e is SupplementalSourceException or InvalidOperationException)
        {
            document.Dispose();
            throw;
        }
        return document;
    }
}
