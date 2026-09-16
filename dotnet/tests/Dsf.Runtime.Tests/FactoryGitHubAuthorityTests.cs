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
    [InlineData("binding-delete")]
    [InlineData("binding-client")]
    [InlineData("grant-corrupt")]
    [InlineData("host-foreign")]
    [InlineData("stale-operation")]
    [InlineData("approval")]
    [InlineData("effect-reference")]
    [InlineData("null-effect-references")]
    [InlineData("observation-only")]
    public async Task Code_tokens_require_complete_current_approval_and_binding_receipts(string damage)
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        var (configuration, manifest) = AuthorityConfiguration(transport);
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(Settings(manifest, transport),
            TestDependencies.Build() with { GitHubAuthorityHttp = http,
                GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock() }, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            CodeHeaders(client.DefaultRequestHeaders, transport, manifest);
            using var before = await client.PostAsync("/tokens/contents", null);
            Assert.True(before.IsSuccessStatusCode, await before.Content.ReadAsStringAsync());
            var key = damage.StartsWith("binding", StringComparison.Ordinal)
                ? configuration.Values.Keys.Single(k => k.Contains("/github-bindings/", StringComparison.Ordinal) && k.EndsWith("/auth", StringComparison.Ordinal))
                : damage is "grant-corrupt" or "host-foreign"
                    ? configuration.Values.Keys.Single(k => k.Contains("/github-operations/", StringComparison.Ordinal)
                        && k.EndsWith(damage == "grant-corrupt" ? "/0001" : "/0003", StringComparison.Ordinal))
                    : damage == "stale-operation" ? ProductConfigurationKeys.FactoryManifest : ProductConfigurationKeys.FactoryGitHubAuthority;
            var record = JsonNode.Parse(configuration.Values[key])!;
            if (damage == "binding-delete") configuration.Values.Remove(key);
            else
            {
                if (damage == "binding-client") record["clientId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
                if (damage == "grant-corrupt") record["principalId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
                if (damage == "host-foreign") record["factoryId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
                if (damage == "stale-operation") record["gitHubAuthorityOperationId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
                if (damage == "approval") record["approvalFingerprint"] = new string('0', 64);
                if (damage == "effect-reference") record["completedEffectHashes"]!["0"] = new string('0', 64);
                if (damage == "null-effect-references") record["completedEffectHashes"] = null;
                if (damage == "observation-only") { record["state"] = "Partial"; record["stage"] = "host-verification"; record["proof"] = null; }
                configuration.Values[key] = record.ToJsonString();
            }
            transport.Paths.Clear();
            using var denied = await client.PostAsync("/tokens/contents", null);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.DoesNotContain(AuthorityTransport.InstallationToken, await denied.Content.ReadAsStringAsync());
            Assert.All(transport.Paths, path => Assert.Equal("/kv", path));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Incomplete_effects_allow_only_operator_non_token_recovery_observation()
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        var (configuration, manifest) = AuthorityConfiguration(transport);
        configuration.Values.Remove(configuration.Values.Keys.Single(k =>
            k.Contains("/github-operations/", StringComparison.Ordinal) && k.EndsWith("/0000", StringComparison.Ordinal)));
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(Settings(manifest, transport),
            TestDependencies.Build() with { GitHubAuthorityHttp = http,
                GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock() }, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var code = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            CodeHeaders(code.DefaultRequestHeaders, transport, manifest);
            using var unauthorized = await code.PostAsync("/verify", null);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
            using var operatorClient = VerificationClient(app.Urls.Single(), transport);
            using var observed = await operatorClient.PostAsync("/verify", null);
            Assert.True(observed.IsSuccessStatusCode, await observed.Content.ReadAsStringAsync());
            Assert.DoesNotContain(AuthorityTransport.InstallationToken, await observed.Content.ReadAsStringAsync());
            using var denied = await code.PostAsync("/tokens/contents", null);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(configuration.Writes);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Removing_a_proven_key_copy_receipt_revokes_code_token_issuance()
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        var (configuration, manifest) = AuthorityConfiguration(transport);
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(Settings(manifest, transport),
            TestDependencies.Build() with { GitHubAuthorityHttp = http,
                GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock() }, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            CodeHeaders(client.DefaultRequestHeaders, transport, manifest);
            using var before = await client.PostAsync("/tokens/contents", null);
            Assert.True(before.IsSuccessStatusCode, await before.Content.ReadAsStringAsync());
            var receipt = configuration.Values.Keys.Single(k => k.Contains("/github-operations/", StringComparison.Ordinal) && k.EndsWith("/0000", StringComparison.Ordinal));
            Assert.True(configuration.Values.Remove(receipt));
            transport.Paths.Clear();
            using var after = await client.PostAsync("/tokens/contents", null);
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
            Assert.DoesNotContain(AuthorityTransport.InstallationToken, await after.Content.ReadAsStringAsync());
            Assert.All(transport.Paths, path => Assert.Equal("/kv", path));
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Auth_preparation_never_turns_a_missing_pin_into_optional_governance(string? pin)
    {
        using var transport = new AuthorityTransport();
        using var http = new HttpClient(transport);
        var (_, manifest) = AuthorityConfiguration(transport);
        var dependencies = TestDependencies.Build() with
        {
            GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential(), GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            Settings(manifest, transport) with { ProfilePin = pin! },
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = VerificationClient(app.Urls.Single(), transport);
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
            Settings(manifest, transport),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = VerificationClient(app.Urls.Single(), transport);
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
    [InlineData("metadata-race", "Unknown")]
    [InlineData("wrong-repository-metadata", "Blocked")]
    [InlineData("denied-repository", "Blocked")]
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
            Settings(manifest, transport),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = VerificationClient(app.Urls.Single(), transport);
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
            Settings(manifest, transport),
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/tokens/" + permission);
            if (authenticated)
            {
                var claims = CodeHeaders(request.Headers, transport, manifest);
                if (claimsDamage == "principal") claims["claims"]![0]!["val"] = "unapproved";
                if (claimsDamage == "tenant") claims["claims"]![1]!["val"] = "unapproved";
                if (claimsDamage == "client") claims["claims"]![2]!["val"] = "unapproved";
                if (claimsDamage == "v2") claims["claims"]![2]!["typ"] = "azp";
                if (claimsDamage == "duplicate") claims["claims"]!.AsArray().Add(claims["claims"]![0]!.DeepClone());
                request.Headers.Remove("X-MS-CLIENT-PRINCIPAL");
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
        var configuration = new TestDependencies.PersistedRuntimeConfiguration();
        foreach (var (key, value) in AuthorityTransport.ReadFixture()) configuration.Values.Add(key, value);
        configuration.Values["dsf/factory/github-authority"] = transport.Authority.ToJsonString();
        var manifest = JsonNode.Parse(configuration.Values[ProductConfigurationKeys.FactoryManifest])!;
        transport.Configuration = configuration.Values;
        return (configuration, manifest);
    }

    private static FactoryGitHubHostSettings Settings(JsonNode manifest, AuthorityTransport transport) =>
        new((string)manifest["identity"]!["productKey"]!,
            "https://dsfo-" + Guid.Parse((string)manifest["identity"]!["factoryId"]!).ToString("N") + ".azconfig.io",
            (string)manifest["profilePin"]!, (string)transport.Authority["authClientId"]!);

    private static HttpClient VerificationClient(string url, AuthorityTransport transport)
    {
        var client = new HttpClient { BaseAddress = new Uri(url) };
        CodeHeaders(client.DefaultRequestHeaders, transport, JsonNode.Parse(AuthorityTransport.ReadFixture()[ProductConfigurationKeys.FactoryManifest])!, true);
        return client;
    }

    private static JsonNode CodeHeaders(System.Net.Http.Headers.HttpRequestHeaders headers, AuthorityTransport transport, JsonNode manifest, bool operatorCaller = false)
    {
        var principal = (operatorCaller ? manifest["identity"]!["azureActorId"]! : transport.Authority["codePrincipalId"]!).GetValue<string>();
        headers.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
        headers.Add("X-MS-CLIENT-PRINCIPAL-ID", principal);
        var claims = JsonSerializer.SerializeToNode(new { auth_typ = "aad", claims = new[]
        {
            new { typ = "oid", val = principal },
            new { typ = "tid", val = (string)manifest["identity"]!["tenantId"]! },
            new { typ = "appid", val = (string)transport.Authority["codeClientId"]! },
        } })!;
        headers.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())));
        return claims;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deployed_path_verifies_approved_App_scope_but_rejects_changed_approval_inputs(bool broaderRequestedPermissions)
    {
        using var transport = new AuthorityTransport { BroaderRequestedPermissions = broaderRequestedPermissions };
        if (broaderRequestedPermissions)
            transport.Authority["expectedAppPermissions"] = JsonNode.Parse("""{"metadata":"read","contents":"write","issues":"write","pull_requests":"write"}""");
        using var http = new HttpClient(transport);
        var (configuration, manifest) = AuthorityConfiguration(transport);
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration) with
        {
            GitHubAuthorityHttp = http,
            GitHubAuthorityCredential = new SyntheticCredential(),
            GitHubAuthorityClock = new AuthorityClock(),
        };
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(
            Settings(manifest, transport), dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = VerificationClient(app.Urls.Single(), transport);
            using var response = await client.PostAsync("/verify", null);
            var body = await response.Content.ReadAsStringAsync();
            if (broaderRequestedPermissions)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.All(transport.Paths, path => Assert.Equal("/kv", path));
                Assert.DoesNotContain(AuthorityTransport.InstallationToken, body);
                return;
            }
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
    public async Task Deployed_repository_lookup_uses_durable_ID_and_current_renamed_branch_paths()
    {
        using var transport = new AuthorityTransport { RepositoryName = "acme/renamed-again", DefaultBranch = "release/2027" };
        using var http = new HttpClient(transport);
        var (_, manifest) = AuthorityConfiguration(transport);
        await using var app = RuntimeVerbs.BuildGitHubAuthenticationHost(Settings(manifest, transport),
            TestDependencies.Build() with { GitHubAuthorityHttp = http, GitHubAuthorityCredential = new SyntheticCredential(),
                GitHubAuthorityClock = new AuthorityClock() }, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = VerificationClient(app.Urls.Single(), transport);
            using var response = await client.PostAsync("/verify", null);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            var proof = JsonNode.Parse(body)!;
            Assert.Equal(transport.RepositoryName, (string?)proof["repository"]);
            Assert.Equal(transport.DefaultBranch, (string?)proof["defaultBranch"]);
            Assert.Equal(2, transport.Paths.Count(p => p == "/repositories/913"));
            Assert.Contains("/repos/acme/renamed-again/git/ref/heads/release%2F2027", transport.Paths);
            Assert.DoesNotContain(transport.Paths, p => p.StartsWith("/repos/acme/renamed/", StringComparison.Ordinal));
            Assert.DoesNotContain(AuthorityTransport.InstallationToken, body);
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
            using var client = VerificationClient(app.Urls.Single(), transport);
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
    public const string InstallationToken = "synthetic-installation-token-never-persist";
    private readonly RSA key = RSA.Create(2048);
    public string? Damage { get; init; }
    public bool BroaderRequestedPermissions { get; init; }
    public string RepositoryName { get; init; } = "acme/renamed";
    public string DefaultBranch { get; init; } = "trunk";
    public List<string> Paths { get; } = [];
    public IReadOnlyDictionary<string, string> Configuration { get; set; } = new Dictionary<string, string>();
    public static Dictionary<string, string> ReadFixture() => JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory-github-authority.json")))!;
    public JsonObject Authority { get; } = JsonNode.Parse(ReadFixture()[ProductConfigurationKeys.FactoryGitHubAuthority])!.AsObject();
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
        if (request.RequestUri.Host == new Uri((string)Authority["destinationSecretUri"]!).Host)
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
                id = (string)Authority["destinationSecretUri"]!, attributes = new { enabled = true } });
        }
        Assert.Equal("api.github.com", request.RequestUri.Host);
        var installation = new { id = 42, app_id = 7, account = new { id = 81, login = "acme", type = "Organization" },
            repository_selection = "selected", suspended_at = (string?)null,
            permissions = new { metadata = "read", contents = "read", issues = "write" } };
        var repository = new { id = 913, full_name = RepositoryName, name = RepositoryName.Split('/')[1],
            owner = new { id = 81, login = "acme" }, default_branch = DefaultBranch, archived = false, disabled = false, has_issues = true };
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
            "/app/installations/42" => Reply(installation),
            _ when path == "/repos/" + RepositoryName + "/installation" => Reply(installation),
            "/installation/repositories" => Reply(new { total_count = 1, repositories = new[] { repository } }),
            "/repositories/913" => Reply(repository),
            _ when path == "/repos/" + RepositoryName + "/git/ref/heads/" + Uri.EscapeDataString(DefaultBranch)
                => Reply(new { @ref = "refs/heads/" + DefaultBranch, @object = new { type = "commit", sha = new string('a', 40) } }),
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
            || Damage == "denied-repository" && path == "/repositories/913"
            || Damage == "denied-key" && path.StartsWith("/secrets/", StringComparison.Ordinal))
            return new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-provider-detail") };
        if (Damage == "malformed" && path == "/app")
            return new(HttpStatusCode.OK) { Content = new StringContent("{secret-provider-detail") };
        if (Damage == "wrong-app" && path == "/app") body["id"] = 8;
        if (Damage == "wrong-repository-metadata" && path == "/repositories/913") body["id"] = 999;
        if (Damage == "metadata-race" && path == "/repositories/913" && Paths.Count(p => p == path) > 1)
            body["default_branch"] = "racing-branch";
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
