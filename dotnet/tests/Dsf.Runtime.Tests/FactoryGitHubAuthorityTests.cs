using Dsf.Runtime;
using Dsf.Runtime.GitHubApp;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Azure.Core;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactoryGitHubAuthorityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Auth_preparation_never_turns_a_missing_pin_into_optional_governance(string? pin)
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        _ = AuthorityConfiguration(transport);
        var dependencies = TestDependencies.Build() with
        {
            GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            new("acme", "https://dsfo-11111111111111111111111111111111.azconfig.io", pin!, AuthorityTransport.ClientId),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync("/verify", null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.All(transport.Paths, path => Assert.Equal("/kv", path));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Auth_module_reads_product_authority_through_its_own_managed_identity_transport()
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        var (_, manifest) = AuthorityConfiguration(transport);
        var dependencies = TestDependencies.Build() with
        {
            GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            new("acme", "https://dsfo-11111111111111111111111111111111.azconfig.io", (string)manifest["profilePin"]!, AuthorityTransport.ClientId),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync("/verify", null);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.Contains("/kv", transport.Paths);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("denied-contents", "Blocked")]
    [InlineData("wrong-app", "Blocked")]
    [InlineData("suspended", "Blocked")]
    [InlineData("wrong-account", "Blocked")]
    [InlineData("wrong-repository", "Blocked")]
    [InlineData("extra-repository", "Blocked")]
    [InlineData("extra-permission", "Unknown")]
    [InlineData("missing-expiry", "Unknown")]
    [InlineData("expired", "Unknown")]
    [InlineData("denied-installation", "Blocked")]
    [InlineData("denied-membership", "Blocked")]
    [InlineData("wrong-membership", "Blocked")]
    [InlineData("wrong-installation", "Blocked")]
    [InlineData("unexpected-installation-permission", "Blocked")]
    [InlineData("denied-key", "Blocked")]
    [InlineData("disabled-key", "Blocked")]
    [InlineData("expanded-installation", "Blocked")]
    [InlineData("unrestricted-token-selection", "Unknown")]
    [InlineData("malformed", "Unknown")]
    public async Task Deployed_verification_never_promotes_denied_or_unverifiable_provider_evidence(string damage, string state)
    {
        using var transport = new AuthorityTransport { Damage = damage };
        using var http = new HttpClient(transport);
        var (configuration, manifest) = AuthorityConfiguration(transport);
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration) with
        {
            GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            new("acme", "https://dsfo-11111111111111111111111111111111.azconfig.io", (string)manifest["profilePin"]!, AuthorityTransport.ClientId),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync("/verify", null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var proof = JsonNode.Parse(body)!;
            Assert.Equal(state, (string?)proof["state"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)proof["operation"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)proof["responsibleAdmin"]));
            Assert.DoesNotContain("secret-provider-detail", body);
            Assert.DoesNotContain(AuthorityTransport.InstallationToken, body);
            Assert.Empty(configuration.Writes);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("contents", false, false, null)]
    [InlineData("contents", true, true, null)]
    [InlineData("issues", true, false, null)]
    [InlineData("contents", true, false, "tenant")]
    [InlineData("contents", true, false, "client")]
    [InlineData("contents", true, false, "principal")]
    [InlineData("contents", true, false, "duplicate")]
    [InlineData("contents", true, false, "malformed")]
    [InlineData("contents", true, true, "v2")]
    public async Task Native_authenticated_code_identity_gets_only_ephemeral_read_tokens_and_never_publication(
        string permission, bool authenticated, bool eligible, string? claimsDamage)
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        var (configuration, manifest) = AuthorityConfiguration(transport);
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration) with
        {
            GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential(),
            GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            new("acme", "https://dsfo-11111111111111111111111111111111.azconfig.io",
                (string)manifest["profilePin"]!, AuthorityTransport.ClientId),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/tokens/" + permission);
            if (authenticated)
            {
                request.Headers.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
                request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "99999999-9999-9999-9999-999999999999");
                var claims = JsonNode.Parse("""
                    {"auth_typ":"aad","claims":[
                        {"typ":"oid","val":"99999999-9999-9999-9999-999999999999"},
                        {"typ":"tid","val":"33333333-3333-3333-3333-333333333333"},
                        {"typ":"appid","val":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                    ]}
                    """)!;
                if (claimsDamage == "principal") claims["claims"]![0]!["val"] = "unapproved";
                if (claimsDamage == "tenant") claims["claims"]![1]!["val"] = "unapproved";
                if (claimsDamage == "client") claims["claims"]![2]!["val"] = "unapproved";
                if (claimsDamage == "v2") claims["claims"]![2]!["typ"] = "azp";
                if (claimsDamage == "duplicate") claims["claims"]!.AsArray().Add(claims["claims"]![0]!.DeepClone());
                request.Headers.Add("X-MS-CLIENT-PRINCIPAL", claimsDamage == "malformed" ? "invalid-base64"
                    : Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())));
            }
            using var response = await client.SendAsync(request);
            Assert.Equal(eligible ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (eligible)
            {
                Assert.Equal(AuthorityTransport.InstallationToken, (string?)JsonNode.Parse(body)!["token"]);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.DoesNotContain("issues", body);
            }
            else
            {
                Assert.All(transport.Paths, path => Assert.Equal("/kv", path));
                Assert.DoesNotContain(AuthorityTransport.InstallationToken, body);
            }
            Assert.Empty(configuration.Writes);
        }
        finally { await app.StopAsync(); }
    }

    private static (TestDependencies.PersistedRuntimeConfiguration Configuration, JsonNode Manifest) AuthorityConfiguration(AuthorityTransport transport)
    {
        var configuration = RuntimeAdmissionTests.Onboarding();
        configuration.Values["dsf/factory/github-authority"] = transport.Authority.ToJsonString();
        var manifest = JsonNode.Parse(configuration.Values[ProductConfigurationKeys.FactoryManifest])!;
        manifest["gitHubAuthorityOperationId"] = "66666666-6666-6666-6666-666666666666";
        configuration.Values[ProductConfigurationKeys.FactoryManifest] = manifest.ToJsonString();
        transport.Configuration = configuration.Values;
        return (configuration, manifest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deployed_path_verifies_actual_App_installation_and_effective_single_repository_read_token(bool broaderRequestedPermissions)
    {
        using var transport = new AuthorityTransport { BroaderRequestedPermissions = broaderRequestedPermissions };
        if (broaderRequestedPermissions)
            transport.Authority["expectedAppPermissions"] = JsonNode.Parse("""{"metadata":"read","contents":"write","issues":"write","pull_requests":"write"}""");
        using var http = new HttpClient(transport);
        var configuration = RuntimeAdmissionTests.Onboarding();
        configuration.Values["dsf/factory/github-authority"] = transport.Authority.ToJsonString();
        var manifest = JsonNode.Parse(configuration.Values[ProductConfigurationKeys.FactoryManifest])!;
        manifest["gitHubAuthorityOperationId"] = "66666666-6666-6666-6666-666666666666";
        configuration.Values[ProductConfigurationKeys.FactoryManifest] = manifest.ToJsonString();
        transport.Configuration = configuration.Values;
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration) with
        {
            GitHubAuthorityHttp = http,
            GitHubAuthorityCredential = new SyntheticCredential(),
            GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            new("acme", "https://dsfo-11111111111111111111111111111111.azconfig.io",
                (string)manifest["profilePin"]!, AuthorityTransport.ClientId), dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync("/verify", null);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            var proof = JsonNode.Parse(body)!;
            Assert.Equal("Verified", (string?)proof["state"]);
            Assert.Equal("acme/renamed", (string?)proof["repository"]);
            Assert.Equal("trunk", (string?)proof["defaultBranch"]);
            Assert.Equal(913, (long?)proof["repositoryId"]);
            Assert.Equal("selected", (string?)proof["installationRepositorySelection"]);
            Assert.Equal("read", (string?)proof["effectivePermissions"]?["contents"]);
            Assert.DoesNotContain("issues", proof["effectivePermissions"]!.ToJsonString());
            Assert.Equal(7, (long?)proof["appId"]);
            Assert.Equal(42, (long?)proof["installationId"]);
            Assert.Contains("/secrets/github-app-private-key/0123456789abcdef0123456789abcdef", transport.Paths);
            Assert.DoesNotContain(AuthorityTransport.InstallationToken, body);
            Assert.DoesNotContain("PRIVATE KEY", body);
            Assert.Empty(configuration.Writes);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Deployed_verification_refuses_missing_authoritative_manifest()
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            new("widget", "https://dsfo-11111111111111111111111111111111.azconfig.io",
                new string('a', 64), "22222222-2222-2222-2222-222222222222"),
            TestDependencies.Build() with { GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential() }, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync("/verify", null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("governance", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Authentication_host_requires_pinned_factory_governance_before_serving()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-github-auth"],
            new Dictionary<string, string?>(), output, error, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("DSF_FACTORY_PROFILE_PIN", error.ToString());
        Assert.Contains("AZURE_CLIENT_ID", error.ToString());
        Assert.DoesNotContain("ready", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AuthorityClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T03:00:00Z");
}

internal sealed class SyntheticCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("synthetic-managed-identity-token", DateTimeOffset.UtcNow.AddHours(1));
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class AuthorityTransport : HttpMessageHandler
{
    public const string ClientId = "77777777-7777-7777-7777-777777777777";
    public const string InstallationToken = "synthetic-installation-token-never-persist";
    private readonly RSA key = RSA.Create(2048);
    public string? Damage { get; init; }
    public bool BroaderRequestedPermissions { get; init; }
    public List<string> Paths { get; } = [];
    public IReadOnlyDictionary<string, string> Configuration { get; set; } = new Dictionary<string, string>();
    public JsonObject Authority { get; } = JsonNode.Parse("""
        {
          "schemaVersion":1,"factoryId":"11111111-1111-1111-1111-111111111111",
          "operationId":"66666666-6666-6666-6666-666666666666",
          "planFingerprint":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "state":"Partial","stage":"host-verification",
          "appId":7,"installationId":42,"accountId":81,"repositoryId":913,
          "expectedPermissions":{"metadata":"read","contents":"read","issues":"write"},
          "expectedAppPermissions":{"metadata":"read","contents":"read","issues":"write"},
          "expectedInstallationRepositorySelection":"selected",
          "sourceVaultId":"/subscriptions/44444444-4444-4444-4444-444444444444/resourcegroups/owner/providers/microsoft.keyvault/vaults/owner",
          "sourceSecretVersion":"abcdef0123456789abcdef0123456789",
          "destinationVaultId":"/subscriptions/44444444-4444-4444-4444-444444444444/resourcegroups/factory/providers/microsoft.keyvault/vaults/factory",
          "destinationSecretUri":"https://factory.vault.azure.net/secrets/github-app-private-key/0123456789abcdef0123456789abcdef",
          "authIdentityId":"/subscriptions/44444444-4444-4444-4444-444444444444/resourcegroups/factory/providers/microsoft.managedidentity/userassignedidentities/github",
          "authPrincipalId":"88888888-8888-8888-8888-888888888888",
          "authClientId":"77777777-7777-7777-7777-777777777777",
          "codePrincipalId":"99999999-9999-9999-9999-999999999999",
          "codeClientId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "acceptedSigningRisk":true
        }
        """)!.AsObject();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        Paths.Add(path);
        if (request.RequestUri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal))
        {
            Assert.Equal("synthetic-managed-identity-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Reply(new { items = Configuration.Select(p => new
            { key = p.Key, label = (string?)null, value = p.Value, etag = "synthetic-etag", content_type = "application/json", locked = false }).ToArray() });
        }
        if (request.RequestUri.Host == "factory.vault.azure.net")
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/secrets/github-app-private-key/0123456789abcdef0123456789abcdef", path);
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.ParseAdd(
                    "Bearer authorization=\"https://login.microsoftonline.com/33333333-3333-3333-3333-333333333333\", resource=\"https://vault.azure.net\"");
                return challenge;
            }
            Assert.Equal("synthetic-managed-identity-token", request.Headers.Authorization.Parameter);
            return Reply(new { value = key.ExportPkcs8PrivateKeyPem(),
                id = "https://factory.vault.azure.net" + path, attributes = new { enabled = true } });
        }
        Assert.Equal("api.github.com", request.RequestUri.Host);
        var installation = new { id = 42, app_id = 7, account = new { id = 81, login = "acme", type = "Organization" },
            repository_selection = "selected", suspended_at = (string?)null,
            permissions = new { metadata = "read", contents = "read", issues = "write" } };
        var repository = new { id = 913, full_name = "acme/renamed", name = "renamed",
            owner = new { id = 81, login = "acme" }, default_branch = "trunk", archived = false, disabled = false, has_issues = true };
        if (request.Method == HttpMethod.Post)
        {
            Assert.Equal("/app/installations/42/access_tokens", path);
            var scope = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[913]"), scope["repository_ids"]));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"metadata":"read","contents":"read"}"""), scope["permissions"]));
            return Reply(new { token = InstallationToken, expires_at = "2026-09-16T04:00:00Z",
                permissions = new { metadata = "read", contents = "read" }, repository_selection = "selected",
                repositories = new[] { repository } }, HttpStatusCode.Created);
        }
        Assert.Equal(HttpMethod.Get, request.Method);
        return path switch
        {
            "/app" when BroaderRequestedPermissions => Reply(new { id = 7, slug = "owner-app",
                permissions = new { metadata = "read", contents = "write", issues = "write", pull_requests = "write" } }),
            "/app" => Reply(new { id = 7, slug = "owner-app", permissions = installation.permissions }),
            "/app/installations/42" or "/repos/acme/renamed/installation" => Reply(installation),
            "/installation/repositories" => Reply(new { total_count = 1, repositories = new[] { repository } }),
            "/repositories/913" => Reply(repository),
            "/repos/acme/renamed/git/ref/heads/trunk" => Reply(new { @ref = "refs/heads/trunk", @object = new { type = "commit", sha = new string('a', 40) } }),
            _ => throw new InvalidOperationException("Unexpected provider path: " + path),
        };
    }
    private HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var path = Paths[^1];
        var body = JsonSerializer.SerializeToNode(value)!;
        if (Damage == "denied-contents" && path.Contains("/git/ref/", StringComparison.Ordinal)
            || Damage == "denied-installation" && path == "/app/installations/42"
            || Damage == "denied-membership" && path == "/repos/acme/renamed/installation"
            || Damage == "denied-key" && path.StartsWith("/secrets/", StringComparison.Ordinal))
            return new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-provider-detail") };
        if (Damage == "malformed" && path == "/app")
            return new(HttpStatusCode.OK) { Content = new StringContent("{secret-provider-detail") };
        if (Damage == "wrong-app" && path == "/app") body["id"] = 8;
        if (Damage == "disabled-key" && path.StartsWith("/secrets/", StringComparison.Ordinal)) body["attributes"]!["enabled"] = false;
        if (Damage == "expanded-installation" && path == "/app/installations/42") body["repository_selection"] = "all";
        if (Damage == "suspended" && path == "/app/installations/42") body["suspended_at"] = "2026-09-15T00:00:00Z";
        if (Damage == "wrong-account" && path == "/app/installations/42") body["account"]!["id"] = 82;
        if (Damage == "wrong-installation" && path == "/app/installations/42") body["id"] = 43;
        if (Damage == "wrong-membership" && path == "/repos/acme/renamed/installation") body["id"] = 43;
        if (Damage == "unexpected-installation-permission" && path == "/app/installations/42") body["permissions"]!["contents"] = "write";
        if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
        {
            if (Damage == "unrestricted-token-selection") body["repository_selection"] = "all";
            if (Damage == "wrong-repository") body["repositories"]![0]!["id"] = 914;
            if (Damage == "extra-repository") body["repositories"]!.AsArray().Add(body["repositories"]![0]!.DeepClone());
            if (Damage == "extra-permission") body["permissions"]!["issues"] = "write";
            if (Damage == "missing-expiry") body.AsObject().Remove("expires_at");
            if (Damage == "expired") body["expires_at"] = "2026-09-15T00:00:00Z";
        }
        return new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
    protected override void Dispose(bool disposing) { if (disposing) key.Dispose(); base.Dispose(disposing); }
}
