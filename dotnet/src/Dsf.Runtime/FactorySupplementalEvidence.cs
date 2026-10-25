using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class FactorySupplementalEvidence(
    SupplementalHostConfiguration configuration, TokenCredential credential, HttpClient http, TimeProvider clock)
    : IFactorySupplementalEvidenceSource
{
    public async Task<SupplementalEvidence> ReadAsync(SupplementalProofRequest request, CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        try
        {
            if (request.CheckId == Guid.Empty || request.ConfigurationRevision != configuration.ConfigurationRevision)
                throw new SupplementalSourceException(SupplementalReason.AuthorityChanged);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var (items, revision) = configuration.Source.Kind switch
            {
                "webiq" => await WebAsync(pending.Token),
                "foundryiq" => await FoundryAsync(pending.Token),
                _ => throw new SupplementalSourceException(SupplementalReason.Unsupported),
            };
            if (clock.GetUtcNow() < started || clock.GetUtcNow() - started >= TimeSpan.FromSeconds(60))
                throw new SupplementalSourceException(SupplementalReason.Incomplete);
            if (items.Any(i => i.Reference.Length > 320))
                throw new SupplementalSourceException(SupplementalReason.CapacityExceeded);
            var proof = Proof(SupplementalState.ReadySupplemental, items.Count == 0 ? SupplementalReason.Empty : SupplementalReason.None,
                items.Select(i => new SupplementalEvidenceReference(i.Reference, FactoryTelemetryValidation.Hash(i.Content))).ToArray(), revision);
            if (JsonSerializer.SerializeToUtf8Bytes(proof, FactoryGitHubJson.Options).Length > 8192)
                throw new SupplementalSourceException(SupplementalReason.CapacityExceeded);
            return new(proof, items);
        }
        catch (SupplementalSourceException e) { return new(Proof(SupplementalState.Blocked, e.Reason, [], null), []); }
        catch (ModelCapabilityException) { return new(Proof(SupplementalState.Blocked, SupplementalReason.IdentityMismatch, [], null), []); }
        catch (OperationCanceledException)
        { return new(Proof(cancellationToken.IsCancellationRequested ? SupplementalState.Canceled : SupplementalState.Unknown,
            cancellationToken.IsCancellationRequested ? SupplementalReason.Canceled : SupplementalReason.NetworkUnavailable, [], null), []); }
        catch (Exception e) when (e is HttpRequestException or Azure.Identity.AuthenticationFailedException)
        { return new(Proof(SupplementalState.Blocked, SupplementalReason.NetworkUnavailable, [], null), []); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return new(Proof(SupplementalState.Blocked, SupplementalReason.MalformedResponse, [], null), []); }

        SupplementalProof Proof(SupplementalState state, SupplementalReason reason, IReadOnlyList<SupplementalEvidenceReference> records, string? revision) =>
            new(configuration.FactoryId, request.CheckId, configuration.ConfigurationRevision, configuration.Source.Kind,
                configuration.SourcePrincipalId, state, reason, started, records, revision, configuration.Source.SecretVersion);
    }

    private async Task<(IReadOnlyList<SupplementalEvidenceItem>, string)> WebAsync(CancellationToken token)
    {
        var (secret, revision) = await SecretAsync(token);
        var response = await new WebIqSearchGateway(http, new Uri("https://api.microsoft.ai/v3/search/web"))
            .RetrieveAsync(secret, configuration.Source.Query, token);
        var results = response.WebResults ?? throw new SupplementalSourceException(SupplementalReason.MalformedResponse);
        if (results.Count > 16) throw new SupplementalSourceException(SupplementalReason.CapacityExceeded);
        var items = results.Select(result =>
        {
            if (result is null || !Uri.TryCreate(result.Url, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0
                || string.IsNullOrWhiteSpace(result.Content) || result.Content.Length > 8192
                || result.Content.Contains(secret, StringComparison.OrdinalIgnoreCase) || SourceContentPolicy.Sensitive(result.Content)
                || result.Title?.Length > 512 || result.Url!.Length > 2048)
                throw new SupplementalSourceException(SupplementalReason.MalformedResponse);
            // Even normalized hostnames can contain credentials; expose no provider-controlled URL text.
            var reference = "webiq:sha256:" + FactoryTelemetryValidation.Hash(result.Url);
            return new SupplementalEvidenceItem(reference, result.Content);
        }).ToArray();
        var (currentSecret, currentRevision) = await SecretAsync(token);
        if (currentRevision != revision || currentSecret != secret)
            throw new SupplementalSourceException(SupplementalReason.AuthorityChanged);
        return (items, revision);
    }

    private async Task<(string Secret, string Revision)> SecretAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.SecretUri + "?api-version=7.4");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            (await Credential().GetTokenAsync(new TokenRequestContext(["https://vault.azure.net/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new SupplementalSourceException(response.StatusCode == HttpStatusCode.NotFound ? SupplementalReason.MissingSecret
                : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? SupplementalReason.PermissionDenied : SupplementalReason.NetworkUnavailable);
        using var body = await EvidencePayload.ReadAsync(response, token, 16 * 1024);
        var native = body.RootElement;
        var attributes = native.GetProperty("attributes");
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var secret = native.GetProperty("value").GetString();
        if (native.GetProperty("id").GetString() != configuration.SecretUri || string.IsNullOrWhiteSpace(secret)
            || secret.Length > 4096 || secret.Any(char.IsControl) || attributes.GetProperty("enabled").ValueKind != JsonValueKind.True
            || attributes.GetProperty("exp").GetInt64() <= now
            || attributes.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now
            || attributes.GetProperty("created").GetInt64() > now || attributes.GetProperty("updated").GetInt64() > now)
            throw new SupplementalSourceException(SupplementalReason.MissingSecret);
        return (secret, FactoryTelemetryValidation.Hash(JsonSerializer.SerializeToElement(new { id = configuration.SecretUri, attributes })));
    }

    private FactoryWorkloadCredential Credential() => new(credential, configuration.TenantId,
        configuration.SourcePrincipalId, configuration.SourceClientId, configuration.SourceIdentityId, clock, requireGuidV2Audience: true);
}
