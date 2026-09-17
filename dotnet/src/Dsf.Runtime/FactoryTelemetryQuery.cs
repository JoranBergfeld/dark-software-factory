using System.Text.Json;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactoryTelemetryQuery(TokenCredential credential, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient BackendHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    public async Task<TelemetryProof> ObserveAsync(FactoryManifest manifest, FactoryTelemetry authority, CancellationToken token) =>
        (await CaptureAsync(manifest, authority, null, [], token)).Proof;

    public Task<TelemetryEvidenceCapture> ObserveEvidenceAsync(FactoryManifest manifest, FactoryTelemetry authority,
        PreviewTelemetrySelection selection, IReadOnlyList<FoundationOperation> effects, CancellationToken token) =>
        CaptureAsync(manifest, authority, selection, effects, token);

    private async Task<TelemetryEvidenceCapture> CaptureAsync(FactoryManifest manifest, FactoryTelemetry authority,
        PreviewTelemetrySelection? selection, IReadOnlyList<FoundationOperation> effects, CancellationToken token)
    {
        var config = authority.Configuration;
        var end = clock.GetUtcNow();
        var start = end.AddSeconds(-config.FreshnessSeconds);
        var records = new List<TelemetryRecordReference>();
        var rows = new List<PreviewTelemetryRow>();
        var observations = new List<TelemetryQueryObservation>();
        var options = new LogsQueryClientOptions { Retry = { MaxRetries = 0 } };
        using var boundedHttp = selection is null ? null : new HttpClient(new BoundedTelemetryTransport(http ?? BackendHttp))
            { Timeout = Timeout.InfiniteTimeSpan };
        if ((boundedHttp ?? http) is { } transport) options.Transport = new HttpClientTransport(transport);
        TokenCredential bound = selection is null ? new BoundCredential(credential, manifest, config)
            : new FactoryWorkloadCredential(credential, manifest.Identity.TenantId, config.SourceIdentity, clock);
        var native = selection is null ? null : new TelemetryEvidenceAuthority(manifest, authority, selection, effects, bound, http ?? BackendHttp);
        if (native is not null) await native.CheckAsync(token);
        var client = new LogsQueryClient(bound, options);
        foreach (var backend in config.Backends)
        foreach (var table in backend.Tables)
        {
            var reason = TelemetryReason.None;
            TelemetryRecordReference? latest = null;
            string? requestId = null;
            var count = 0;
            try
            {
                (latest, count, requestId) = await QueryAsync(false);
                if (latest is not null) records.Add(latest);
                else
                {
                    (latest, _, _) = await QueryAsync(true);
                    if (latest is not null && latest.RecordTime >= start)
                        throw new QueryFailure(TelemetryReason.PartialResponse);
                    reason = latest is null ? TelemetryReason.Empty : TelemetryReason.Stale;
                }
            }
            catch (QueryFailure e) { reason = e.Reason; }
            catch (Azure.RequestFailedException e)
            { reason = e.Status == 0 ? TelemetryReason.NetworkUnavailable : e.Status is 401 or 403 ? TelemetryReason.PermissionDenied
                : e.ErrorCode?.Contains("Partial", StringComparison.OrdinalIgnoreCase) == true ? TelemetryReason.PartialResponse
                : TelemetryReason.MalformedResponse; }
            catch (Azure.Identity.AuthenticationFailedException) { reason = TelemetryReason.AmbiguousAuthority; }
            catch (HttpRequestException) { reason = TelemetryReason.NetworkUnavailable; }
            catch (OperationCanceledException)
            {
                reason = token.IsCancellationRequested ? TelemetryReason.AuthorityChanged : TelemetryReason.NetworkUnavailable;
                observations.Add(new(backend.ResourceId, table, reason, QueryHash(), requestId, count, latest));
                return Capture(token.IsCancellationRequested ? TelemetryState.Canceled : TelemetryState.Unknown, reason);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException
                or IndexOutOfRangeException or KeyNotFoundException)
            { reason = TelemetryReason.MalformedResponse; }
            observations.Add(new(backend.ResourceId, table, reason, QueryHash(), requestId, count, latest));

            string QueryHash() => FactoryTelemetryValidation.Hash((selection?.Queries ?? config.Queries)[table]);

            async Task<(TelemetryRecordReference? Latest, int Count, string? RequestId)> QueryAsync(bool staleProbe)
            {
                await CheckBackendAsync(bound, backend, table, native, token);
                var response = await client.QueryWorkspaceAsync(backend.WorkspaceId,
                    staleProbe ? config.StaleQueries[table] : (selection?.Queries ?? config.Queries)[table],
                    new QueryTimeRange(staleProbe ? DateTimeOffset.UnixEpoch : start, end),
                    new LogsQueryOptions { AllowPartialErrors = false }, token);
                var result = response.Value;
                if (result.Status != LogsQueryResultStatus.Success || result.AllTables.Count != 1 || result.Table.Rows.Count >= 500000
                    || staleProbe && result.Table.Rows.Count > 1
                    || selection is not null && !staleProbe && result.Table.Rows.Count > selection.RowsPerTable)
                    throw new QueryFailure(TelemetryReason.PartialResponse);
                var columns = result.Table.Columns;
                var projected = selection is not null && !staleProbe ? FactoryPreviewTelemetryValidation.Fields(table) : [];
                if (columns.Count != 5 + projected.Count || columns[0].Name != "TimeGenerated" || columns[0].Type != LogsColumnType.Datetime
                    || columns[1].Name != "_ResourceId" || columns[1].Type != LogsColumnType.String
                    || columns[2].Name != "_ItemId" || columns[2].Type != LogsColumnType.String
                    || columns[3].Name != "TenantId" || columns[3].Type != LogsColumnType.String
                    || columns[4].Name != "Type" || columns[4].Type != LogsColumnType.String)
                    throw new JsonException("Native schema required.");
                for (var i = 0; i < projected.Count; i++)
                    if (columns[i + 5].Name != projected[i].Name || columns[i + 5].Type.ToString() != projected[i].Type)
                        throw new JsonException("Exact native content schema required.");
                TelemetryRecordReference? newest = null;
                var captured = new List<PreviewTelemetryRow>();
                foreach (var row in result.Table.Rows)
                {
                    if (row.Count != columns.Count) throw new JsonException("Native row does not match its schema.");
                    var time = row.GetDateTimeOffset(0) ?? throw new JsonException();
                    var resource = row.GetString(1) ?? throw new JsonException();
                    var item = row.GetString(2) ?? throw new JsonException();
                    if (time < (staleProbe ? DateTimeOffset.UnixEpoch : start) || time > end
                        || !backend.ApplicationResourceIds.Contains(resource, StringComparer.OrdinalIgnoreCase)
                        || !string.Equals(row.GetString(3), backend.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                        || row.GetString(4) != table)
                        throw new QueryFailure(TelemetryReason.WrongScope);
                    if (string.IsNullOrWhiteSpace(item) || item.Length > FactoryTelemetryLimits.ItemIdCharacters || item.Any(char.IsControl)) throw new JsonException();
                    if (selection is not null && TelemetryContentPolicy.Sensitive(item)) throw new QueryFailure(TelemetryReason.SensitiveContent);
                    var reference = new TelemetryRecordReference(backend.ResourceId, backend.WorkspaceId, table,
                        backend.ApplicationResourceIds.Single(id => string.Equals(id, resource, StringComparison.OrdinalIgnoreCase)), time, item, result.Table.Rows.Count);
                    if (newest is null || newest.RecordTime < time) newest = reference;
                    if (projected.Count > 0)
                    {
                        var fields = projected.Select((field, i) => (field.Name, Value: JsonSerializer.SerializeToElement(row[i + 5])))
                            .ToDictionary(f => f.Name, f => f.Value);
                        FactoryPreviewTelemetryValidation.ValidateFields(table, fields);
                        if (fields.Values.Any(v => v.ValueKind == JsonValueKind.String && TelemetryContentPolicy.Sensitive(v.GetString()!)))
                            throw new QueryFailure(TelemetryReason.SensitiveContent);
                        captured.Add(new(reference, QueryHash(), FactoryModelsValidation.Hash(fields), fields));
                    }
                }
                var id = response.GetRawResponse().Headers.TryGetValue("x-ms-request-id", out var header)
                    && Guid.TryParse(header, out var parsed) ? parsed.ToString() : null;
                await CheckBackendAsync(bound, backend, table, native, token);
                rows.AddRange(captured);
                return (newest, result.Table.Rows.Count, id);
            }
        }
        var failure = observations.FirstOrDefault(o => o.Reason is not (TelemetryReason.None or TelemetryReason.Empty or TelemetryReason.Stale));
        var ready = failure is null && config.Backends.All(b => records.Any(r => r.BackendResourceId == b.ResourceId));
        if (native is not null && ready)
        {
            await native.CheckAsync(token);
            foreach (var backend in config.Backends)
            foreach (var table in backend.Tables) await CheckBackendAsync(bound, backend, table, native, token);
        }
        return Capture(ready ? TelemetryState.Ready : TelemetryState.Blocked, ready ? TelemetryReason.None
            : failure?.Reason ?? (observations.Any(o => o.Reason == TelemetryReason.Stale) ? TelemetryReason.Stale : TelemetryReason.Empty));

        TelemetryEvidenceCapture Capture(TelemetryState state, TelemetryReason reason) =>
            new(Proof(state, reason), state == TelemetryState.Ready ? rows : [], native?.Revisions ?? new(StringComparer.Ordinal));

        TelemetryProof Proof(TelemetryState state, TelemetryReason reason) => new(state, reason,
            config.FactoryId, authority.OperationId, authority.ApprovalFingerprint, config.SourceIdentity.PrincipalId!,
            config.ApplicationEnvironment, clock.GetUtcNow(), start, end, records,
            state == TelemetryState.Ready ? "Real source managed-identity query completed; native application records observed. " + FactoryTelemetryValidation.Coverage
                : $"NOT READY: {reason}. Source/evidence/network administrator: inspect the exact backend/table and managed identity in this proof; no repair, synthetic data, or application-health diagnosis.")
            { Queries = observations, CheckId = authority.CheckId,
                CoverageLimitations = selection is null ? FactoryTelemetryValidation.Coverage : FactoryPreviewTelemetryValidation.Coverage };
    }

    private async Task CheckBackendAsync(TokenCredential bound, TelemetryBackend backend, string table,
        TelemetryEvidenceAuthority? evidence, CancellationToken token)
    {
        foreach (var target in new[] { backend.ResourceId, backend.ResourceId + "/tables/" + table })
        {
            var resource = evidence is null ? await ReadLegacyAsync() : await evidence.ReadAsync(target, FactoryTelemetryValidation.BackendApiVersion, token);
            if (!string.Equals(resource.GetProperty("id").GetString(), target, StringComparison.OrdinalIgnoreCase)
                || target == backend.ResourceId && resource.GetProperty("properties").GetProperty("customerId").GetString() != backend.WorkspaceId)
                throw new QueryFailure(TelemetryReason.WrongScope);
            if (!(target == backend.ResourceId ? FactoryTelemetryValidation.HasSupportedWorkspaceAuthorization(resource)
                : FactoryTelemetryValidation.HasSupportedTableAuthorization(resource)))
                throw new QueryFailure(TelemetryReason.Unsupported);

            async Task<JsonElement> ReadLegacyAsync()
            {
                var access = await bound.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target
                    + "?api-version=" + FactoryTelemetryValidation.BackendApiVersion);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                using var response = await (http ?? BackendHttp).SendAsync(request, token);
                if (!response.IsSuccessStatusCode) throw new Azure.RequestFailedException((int)response.StatusCode, "Backend authorization metadata unavailable.");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token),
                    new JsonDocumentOptions { AllowDuplicateProperties = false });
                return json.RootElement.Clone();
            }
        }
    }

    private sealed class QueryFailure(TelemetryReason reason) : Exception
    {
        public TelemetryReason Reason { get; } = reason;
    }
    private sealed class BoundCredential(TokenCredential inner, FactoryManifest manifest, FactoryTelemetryConfiguration configuration) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var access = await inner.GetTokenAsync(requestContext, cancellationToken);
            try
            {
                var part = access.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
                using var json = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')),
                    new JsonDocumentOptions { AllowDuplicateProperties = false });
                var claims = json.RootElement;
                var source = configuration.SourceIdentity;
                if (claims.GetProperty("oid").GetString() != source.PrincipalId
                    || claims.GetProperty("tid").GetString() != manifest.Identity.TenantId
                    || claims.GetProperty("appid").GetString() != source.ClientId
                    || !string.Equals(claims.GetProperty("xms_mirid").GetString(), source.ResourceId, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException();
            }
            catch (Exception e) when (e is JsonException or FormatException or IndexOutOfRangeException or KeyNotFoundException)
            { throw new Azure.Identity.AuthenticationFailedException("Actual managed-identity token does not match the approved source binding."); }
            return access;
        }
    }
}
