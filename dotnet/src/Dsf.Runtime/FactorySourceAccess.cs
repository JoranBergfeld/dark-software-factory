using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal class SourceAcquisitionException(SourceState state, string reason) : Exception(reason)
{
    internal SourceState State { get; } = state;
}

internal sealed class FactorySourceAccess(FactoryHostSettings settings, TokenCredential credential, HttpClient http, TimeProvider clock)
{
    internal FactoryManifest Manifest { get; private set; } = null!;
    internal FactoryGitHubAuthority GitHub { get; private set; } = null!;
    internal FactorySource Source { get; private set; } = null!;
    internal FoundationOperation Code { get; private set; } = null!;
    internal TokenCredential Credential { get; private set; } = null!;
    private string? pinned;

    internal async Task CheckAsync(CancellationToken token)
    {
        var gateway = new AzureConfigurationSettingsGateway(pinned is null ? credential : Credential, http);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (key, value) in gateway.ListAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
        {
            if (values.Count >= 512 || !values.TryAdd(key, value)) throw new JsonException();
        }
        var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
            values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), settings.Product, settings.ProductEndpoint, settings.ProfilePin);
        var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
        FactoryGitHubAuthorityValidation.ReadCompletedEffects(github, values);
        FactoryGitHubAuthorityValidation.ValidateVerified(github);
        var (source, effects) = FactorySourceValidation.Read(manifest, github, values);
        var revision = FactoryModelsValidation.Hash(new { manifest, github, source });
        if (pinned is not null && pinned != revision) throw new SourceAcquisitionException(SourceState.Blocked, "AuthorityChanged");
        Manifest = manifest;
        GitHub = github;
        Source = source;
        Code = FactorySourceValidation.ReadRecord<FoundationOperation>(values, ProductConfigurationKeys.GitHubAuthorityBinding(github.OperationId, "code"));
        if (Code.ClientId != settings.ManagedIdentityClientId) throw new JsonException();
        Credential = new FactoryWorkloadCredential(credential, manifest.Identity.TenantId, Code, clock);
        foreach (var binding in source.Configuration.Infrastructure)
        {
            using var native = await ArmAsync(binding.Receipt.Target, binding.Receipt.ApiVersion, token);
            if (FactorySourceValidation.InfrastructureIncarnation(native.RootElement, binding.Receipt.Target) != binding.Incarnation)
                throw new SourceAcquisitionException(SourceState.Blocked, "FoundationNetworkOrDnsChanged");
        }
        var hosting = FactorySourceValidation.ReadRecord<FoundationOperation>(values, ProductConfigurationKeys.GitHubAuthorityBinding(github.OperationId, "hosting"));
        foreach (var binding in new[] { Code, hosting })
        {
            using var native = await ArmAsync(binding.Target, binding.ApiVersion, token);
            var body = native.RootElement;
            if (!string.Equals(body.GetProperty("id").GetString(), binding.Target, StringComparison.OrdinalIgnoreCase)
                || body.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset()
                    != DateTimeOffset.Parse(binding.Incarnation!, System.Globalization.CultureInfo.InvariantCulture))
                throw new JsonException();
            if (binding == Code)
            {
                var properties = body.GetProperty("properties");
                if (properties.GetProperty("principalId").GetString() != Code.PrincipalId
                    || properties.GetProperty("clientId").GetString() != Code.ClientId
                    || properties.GetProperty("tenantId").GetString() != manifest.Identity.TenantId) throw new JsonException();
            }
            else if (body.GetProperty("properties").GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True) throw new JsonException();
        }
        var resources = new Dictionary<string, JsonElement>();
        foreach (var effect in effects)
        {
            using var native = await ArmAsync(effect.Target, effect.ApiVersion, token);
            if (FactorySourceValidation.ObserveResource(native.RootElement, effect.Target, manifest, github) != effect.Incarnation
                || FactorySourceValidation.NativeRevision(native.RootElement, effect.Target) != effect.NativeRevision)
                throw new SourceAcquisitionException(SourceState.Blocked, "SourceGrantOrIncarnationChanged");
            resources.Add(effect.Target, native.RootElement.Clone());
        }
        var target = FactorySourceValidation.HostTarget(github);
        if (FactoryHostedWorkloadPolicy.Validate(resources[target], resources[target + "/authconfigs/current"],
            Code, hosting, source.Configuration.Selection.RuntimeImage, "serve-source", settings.Product, settings.ProductEndpoint,
            settings.ProfilePin, manifest.Identity.TenantId, source.Configuration.Selection.AudienceClientId,
            [manifest.Identity.AzureActorId, Code.PrincipalId!]) != source.HostEndpoint) throw new JsonException();
        var grants = FactorySourceValidation.Grants(manifest, github);
        foreach (var grant in grants)
        {
            using var native = await ArmAsync(grant.Scope + "/providers/microsoft.authorization/roledefinitions/" + grant.RoleDefinitionId.Split('/').Last(), "2022-04-01", token);
            FactorySourceValidation.ValidateRole(native.RootElement, grant);
        }
        var assignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in grants)
        {
            using var roles = await ArmAsync(grant.Scope
                + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + Code.PrincipalId + "')", "2022-04-01", token);
            if (roles.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
                throw new JsonException("Incomplete native authority inventory.");
            foreach (var role in roles.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = role.GetProperty("id").GetString()!;
                if (!grants.Any(g => string.Equals(g.Target, id, StringComparison.OrdinalIgnoreCase))) throw new JsonException();
                assignments.Add(id);
            }
        }
        if (assignments.Count != grants.Count) throw new JsonException();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (key, value) in new AzureConfigurationSettingsGateway(Credential, http)
            .ListAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
            if (current.Count >= 512 || !current.TryAdd(key, value)) throw new JsonException();
        var keys = new[] { ProductConfigurationKeys.FactoryManifest, ProductConfigurationKeys.FactoryProfile,
            ProductConfigurationKeys.FactorySource, ProductConfigurationKeys.FactoryGitHubAuthority }
            .Concat(FactoryGitHubAuthorityValidation.BindingNames.Select(n => ProductConfigurationKeys.GitHubAuthorityBinding(github.OperationId, n)))
            .Concat(Enumerable.Range(0, 5).Select(i => ProductConfigurationKeys.GitHubAuthorityOperation(github.OperationId, i)))
            .Concat(Enumerable.Range(0, 8).Select(i => ProductConfigurationKeys.SourceOperation(source.OperationId, i)));
        if (keys.Any(key => !current.TryGetValue(key, out var value) || value != values[key]))
            throw new SourceAcquisitionException(SourceState.Blocked, "AuthorityChanged");
        pinned = revision;
    }

    internal async Task<JsonDocument> ArmAsync(string target, string api, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target + (target.Contains('?') ? "&" : "?") + "api-version=" + api);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await Credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), token)).Token);
        return await SendAsync(request, 1024 * 1024, token);
    }

    internal async Task<string> TokenAsync(CancellationToken token)
    {
        await CheckAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHub.HostEndpoint + "/tokens/contents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await Credential.GetTokenAsync(
            new TokenRequestContext(["api://" + GitHub.AudienceClientId + "/.default"]), token)).Token);
        using var body = await SendAsync(request, 16384, token);
        var json = body.RootElement;
        var expiry = json.GetProperty("expires_at").GetDateTimeOffset();
        var repositories = json.GetProperty("repository_ids").EnumerateArray().Select(v => v.GetInt64()).ToArray();
        var permissions = json.GetProperty("permissions");
        if (!repositories.SequenceEqual([GitHub.RepositoryId]) || expiry <= clock.GetUtcNow().AddSeconds(30)
            || expiry > clock.GetUtcNow().AddHours(1).AddSeconds(30) || permissions.EnumerateObject().Count() != 2
            || permissions.GetProperty("metadata").GetString() != "read" || permissions.GetProperty("contents").GetString() != "read")
            throw new SourceAcquisitionException(SourceState.Blocked, "InvalidReadOnlyAuthorityToken");
        var access = json.GetProperty("token").GetString() is { Length: > 0 and <= 4096 } value ? value : throw new JsonException();
        await CheckAsync(token);
        return access;
    }

    internal async Task<JsonDocument> SendAsync(HttpRequestMessage request, int maximumBytes, CancellationToken token)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new SourceAcquisitionException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? SourceState.Blocked : SourceState.Partial, response.StatusCode == HttpStatusCode.NotFound ? "Unavailable" : "ProviderAccessDeniedOrUnavailable");
        if (response.Content.Headers.ContentLength > maximumBytes) throw new SourceAcquisitionException(SourceState.Partial, "ResponseLimit");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, token);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new SourceAcquisitionException(SourceState.Partial, "ResponseLimit");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
    }
}
