using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class TelemetryEvidenceAuthority(FactoryManifest manifest, FactoryTelemetry authority, PreviewTelemetrySelection selection,
    IReadOnlyList<FoundationOperation> effects, TokenCredential credential, HttpClient http)
{
    internal SortedDictionary<string, string> Revisions { get; } = new(StringComparer.Ordinal);

    internal async Task<JsonElement> ReadAsync(string target, string api, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target
            + (target.Contains('?') ? "&" : "?") + "api-version=" + api);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new Azure.RequestFailedException((int)response.StatusCode, "Current telemetry authority unavailable.");
        using var body = await EvidencePayload.ReadAsync(response, token, FactoryPreviewTelemetryValidation.ResponseBytes);
        var result = body.RootElement.Clone();
        var hash = FactoryModelsValidation.Hash(result);
        if (Revisions.TryGetValue(target, out var previous) && previous != hash)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentTelemetryAuthorityChanged");
        Revisions[target] = hash;
        return result;
    }

    internal async Task CheckAsync(CancellationToken token)
    {
        foreach (var backend in authority.Configuration.Backends)
        {
            var grant = authority.Configuration.AccessGrants.Single(g => g.Scope == backend.ResourceId);
            var receipt = effects.Single(e => e.Target == grant.Target);
            FactoryPreviewTelemetryValidation.RequireGrant(await ReadAsync(grant.Target, "2022-04-01", token), grant, receipt, manifest.Identity.AzureActorId);
            FactoryPreviewTelemetryValidation.RequireRole(await ReadAsync(backend.ResourceId + "/providers/microsoft.authorization/roledefinitions/"
                + grant.RoleDefinitionId.Split('/').Last(), "2022-04-01", token), grant.RoleDefinitionId, selection.AccessRoleRevisions[grant.RoleDefinitionId]);
            var list = await ReadAsync(backend.ResourceId + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('"
                + grant.PrincipalId + "')", "2022-04-01", token);
            if (list.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
                throw new JsonException("Incomplete telemetry grant inventory.");
            var assignments = list.GetProperty("value").EnumerateArray().ToArray();
            if (assignments.Length != 1) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "TelemetryBackendScopeExpandedOrMissing");
            FactoryPreviewTelemetryValidation.RequireGrant(assignments[0], grant, receipt, manifest.Identity.AzureActorId);
            foreach (var app in backend.ApplicationResourceIds)
            {
                var component = await ReadAsync(app, "2020-02-02", token);
                if (!string.Equals(component.GetProperty("id").GetString(), app, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(component.GetProperty("properties").GetProperty("WorkspaceResourceId").GetString(), backend.ResourceId, StringComparison.OrdinalIgnoreCase))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "TelemetryApplicationBackendChanged");
            }
        }
    }
}

internal static class TelemetryContentPolicy
{
    internal static bool Sensitive(string value) => SourceContentPolicy.Sensitive(value) || Regex.IsMatch(value,
        @"https?://|[?&][A-Za-z][^\s&=]{0,80}=|[^\s@]+@[^\s@]+\.[^\s@]+|\b(?:\d{1,3}\.){3}\d{1,3}\b|"
        + @"(?i:bearer\s+\S+|password|passwd|client_secret|api[_-]?key|access[_-]?token|accountkey|sharedaccesskey|authorization|cookie)\s*[:=]?\s*\S+|"
        + @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static void RequireSafe(TelemetryEvidenceCapture capture)
    {
        if (capture.Rows.Any(row => Sensitive(row.Reference.ItemId)
                || row.Fields.Values.Any(v => v.ValueKind == JsonValueKind.String && Sensitive(v.GetString()!)))
            || capture.Proof.Records.Any(r => Sensitive(r.ItemId))
            || capture.Proof.Queries.Any(q => q.LatestRecord is { } record && Sensitive(record.ItemId)))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "SensitiveTelemetryContent");
    }
}

internal sealed class BoundedTelemetryTransport(HttpClient inner) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var outgoing = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            outgoing.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(token));
            foreach (var header in request.Content.Headers) outgoing.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var response = await inner.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            using var body = await EvidencePayload.ReadAsync(response, token, FactoryPreviewTelemetryValidation.ResponseBytes);
            response.Content.Dispose();
            response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.RootElement.GetRawText()));
            response.Content.Headers.ContentType = new("application/json");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
