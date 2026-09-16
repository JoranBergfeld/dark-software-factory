using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

internal sealed class SourceInventoryTransport : HttpMessageHandler
{
    internal const string SourceKey = "dsf/factory/source";
    internal const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string InstallationToken = "source-installation-token-never-persist";
    internal readonly Dictionary<string, string> Values;
    internal readonly Dictionary<string, (string Value, int Revision)> Progress = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsonNode> Resources = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, JsonNode> Trees = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);
    internal readonly List<string> Requests = [];
    internal readonly List<string> Writes = [];
    internal readonly List<JsonNode> BrokerClaims = [];
    internal string Branch = "actual-default";
    internal string Repository = "acme/renamed";
    internal string Head = Commit;
    internal string RootTree;
    internal FactoryManifest Manifest;
    internal FactoryGitHubAuthority GitHub;
    internal FactorySource Authority;
    internal readonly SourceTestClock Clock = new();
    internal Action<HttpRequestMessage>? OnRequest { get; set; }
    internal Action<string, string>? AfterWrite { get; set; }
    internal bool V2Tokens { get; set; }
    internal Action<JsonObject>? ChangeApiToken { get; set; }
    internal int GitHubStatus { get; set; } = 200;
    internal int ProgressPageSize { get; set; } = 100;
    internal int EmptyProgressPages { get; set; }
    internal Action<int, HttpResponseMessage>? ChangeProgressPage { get; set; }
    internal Action? AfterBlobResponse { get; set; }
    internal void ImportProgress(SourceInventoryTransport source)
    {
        var serialized = JsonSerializer.Serialize(source.Progress.ToDictionary(p => p.Key, p => new { p.Value.Value, p.Value.Revision }));
        var imported = JsonNode.Parse(serialized)!.AsObject();
        foreach (var (key, item) in imported) Progress[key] = ((string)item!["Value"]!, (int)item["Revision"]!);
        revision = Progress.Values.Select(p => p.Revision).DefaultIfEmpty().Max();
    }
    private int revision;
    internal string Group => GitHub.FactoryResourceGroupId!;
    internal string Prefix => "df-" + Manifest.Identity.FactoryId.ToString("N")[..14];
    internal string ProgressEndpoint => "https://dsfs-" + Manifest.Identity.FactoryId.ToString("N") + ".azconfig.io";
    internal string ProductEndpoint => "https://dsfo-" + Manifest.Identity.FactoryId.ToString("N") + ".azconfig.io";
    internal string HostTarget => Group + "/providers/microsoft.app/containerapps/" + Prefix + "-source";
    internal string StoreTarget => Group + "/providers/microsoft.appconfiguration/configurationstores/dsfs-" + Manifest.Identity.FactoryId.ToString("N");
    internal FoundationOperation Code => JsonSerializer.Deserialize<FoundationOperation>(
        Values[ProductConfigurationKeys.GitHubAuthorityBinding(GitHub.OperationId, "code")], FactoryGitHubJson.Options)!;
    internal Dictionary<string, string?> Environment => new()
    {
        ["DSF_PRODUCT"] = Manifest.Identity.ProductKey, ["AZURE_APPCONFIG_ENDPOINT"] = ProductEndpoint,
        ["DSF_FACTORY_PROFILE_PIN"] = Manifest.ProfilePin, ["AZURE_CLIENT_ID"] = GitHub.CodeClientId,
    };

    internal SourceInventoryTransport(Dictionary<string, string>? values = null, DateTimeOffset? now = null, bool nativeIdCasing = false)
    {
        Values = values is null ? JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory-github-authority.json")))!
            : new(values, StringComparer.Ordinal);
        if (now is not null) Clock.Now = now.Value;
        Manifest = JsonSerializer.Deserialize<FactoryManifest>(Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        GitHub = JsonSerializer.Deserialize<FactoryGitHubAuthority>(Values[ProductConfigurationKeys.FactoryGitHubAuthority], FactoryGitHubJson.Options)!;
        var prepared = Values.TryGetValue(SourceKey, out var existing) ? JsonSerializer.Deserialize<FactorySource>(existing, FactoryGitHubJson.Options) : null;
        var operation = prepared?.OperationId ?? Guid.Parse("dededede-dede-dede-dede-dededededede");
        var config = prepared?.Configuration ?? new SourceConfiguration(1, Manifest.Identity.FactoryId, Manifest.Identity.PlanFingerprint, Manifest.ProfilePin,
            GitHub.OperationId, GitHub.ApprovalFingerprint!, new(GitHub.RuntimeImage!, "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), new string('c', 64),
            FactorySourceValidation.InfrastructureTargets(GitHub).Select((target, index) => new SourceInfrastructureBinding(
                new(1, "decide-foundation-operation", Manifest.Identity.FactoryId, GitHub.FoundationOperationId, index, target,
                    index == 0 ? "2024-05-01" : "2024-06-01", new string('a', 64), new Guid(index + 1, 0, 0, new byte[8]), Clock.Now,
                    FoundationOperationState.Completed, FoundationResourceDisposition.Created)
                { ResourceId = target, Incarnation = Clock.Now.ToString("O"), ProviderOperationId = "a" }, Clock.Now.ToString("O"))).ToArray());
        var approval = FactoryModelsValidation.Hash(config);
        var manifest = JsonSerializer.SerializeToNode(Manifest, FactoryGitHubJson.Options)!;
        manifest["sourceOperationId"] = operation;
        manifest["sourceApprovalFingerprint"] = approval;
        Values[ProductConfigurationKeys.FactoryManifest] = manifest.ToJsonString();
        var created = Clock.Now.ToString("O");
        var system = new { createdAt = created, lastModifiedAt = created, createdBy = Manifest.Identity.AzureActorId };
        foreach (var binding in config.Infrastructure)
            Resources[binding.Receipt.Target] = JsonSerializer.SerializeToNode(new { id = binding.Receipt.Target,
                systemData = new { createdAt = binding.Incarnation }, properties = new { } })!;
        foreach (var name in new[] { "code", "hosting" })
        {
            var binding = JsonSerializer.Deserialize<FoundationOperation>(
                Values[ProductConfigurationKeys.GitHubAuthorityBinding(GitHub.OperationId, name)], FactoryGitHubJson.Options)!;
            Resources[binding.Target] = JsonSerializer.SerializeToNode(new
            {
                id = binding.Target, systemData = new { createdAt = binding.Incarnation, lastModifiedAt = binding.Incarnation },
                properties = new { principalId = binding.PrincipalId, clientId = binding.ClientId, tenantId = Manifest.Identity.TenantId,
                    vnetConfiguration = new { @internal = true } },
            })!;
        }
        Resources[StoreTarget] = JsonSerializer.SerializeToNode(new
        {
            id = StoreTarget, systemData = system, sku = new { name = "Standard" },
            properties = new { endpoint = ProgressEndpoint, publicNetworkAccess = "Disabled", disableLocalAuth = true, provisioningState = "Succeeded" },
        })!;
        var network = Group + "/providers/microsoft.network/virtualnetworks/" + Prefix + "-network";
        var endpoint = Group + "/providers/microsoft.network/privateendpoints/" + Prefix + "-source-config";
        Resources[endpoint] = JsonSerializer.SerializeToNode(new { id = endpoint, systemData = system,
            properties = new { provisioningState = "Succeeded", subnet = new { id = nativeIdCasing
                    ? (network + "/subnets/private-endpoints").ToUpperInvariant() : network + "/subnets/private-endpoints" },
                privateLinkServiceConnections = new[] { new { properties = new { privateLinkServiceId = StoreTarget,
                    groupIds = new[] { "configurationStores" }, privateLinkServiceConnectionState = new { status = "Approved" } } } } } })!;
        Resources[endpoint + "/privatednszonegroups/default"] = JsonSerializer.SerializeToNode(new
        {
            id = endpoint + "/privatednszonegroups/default", systemData = system,
            properties = new { provisioningState = "Succeeded", privateDnsZoneConfigs = new[] { new { properties = new
                { privateDnsZoneId = Group + "/providers/microsoft.network/privatednszones/privatelink.azconfig.io" } } } },
        })!;
        var grants = new[] { (Group, "acdd72a7-3385-48ef-bd42-f606fba81ae7"),
            (Group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + Manifest.Identity.FactoryId.ToString("N"), "516239f1-63e1-4d78-a4de-a74fb236a071"),
            (StoreTarget, "5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b") };
        foreach (var (scope, role) in grants)
        {
            var roleId = "/subscriptions/" + Manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/" + role;
            var grant = FactoryModelsValidation.Grant(scope, roleId, GitHub.CodePrincipalId, "");
            Resources[grant.Target] = JsonSerializer.SerializeToNode(new
            {
                id = grant.Target, properties = new { scope, roleDefinitionId = roleId, principalId = GitHub.CodePrincipalId,
                    principalType = "ServicePrincipal", createdOn = created, updatedOn = created, createdBy = Manifest.Identity.AzureActorId,
                    condition = (string?)null, conditionVersion = (string?)null },
            })!;
            Resources[roleId] = JsonSerializer.SerializeToNode(new
            {
                id = roleId, properties = new { type = "BuiltInRole", permissions = new[] { new
                {
                    actions = role == grants[0].Item2 ? new[] { "*/read" } : [],
                    notActions = Array.Empty<string>(), notDataActions = Array.Empty<string>(),
                    dataActions = role == grants[0].Item2 ? [] : role == grants[1].Item2
                        ? new[] { "Microsoft.AppConfiguration/configurationStores/keyValues/read", "Microsoft.AppConfiguration/configurationStores/label/read" }
                        : new[] { "Microsoft.AppConfiguration/configurationStores/*" },
                } } },
            })!;
        }
        Resources[HostTarget] = JsonSerializer.SerializeToNode(new
        {
            id = HostTarget, systemData = system,
            identity = new { type = "UserAssigned", userAssignedIdentities = new Dictionary<string, object> { [Code.Target] = new { } } },
            properties = new
            {
                managedEnvironmentId = GitHub.HostingEnvironmentId,
                configuration = new { activeRevisionsMode = "Single", ingress = new
                    { external = true, allowInsecure = false, targetPort = 8080, fqdn = "source.private.azurecontainerapps.io" } },
                template = new { containers = new[] { new { image = config.Selection.RuntimeImage,
                    command = new[] { "dotnet", "dsf-runtime.dll" }, args = new[] { "serve-source" },
                    env = Environment.Select(p => new { name = p.Key, value = p.Value }).ToArray() } } },
            },
        })!;
        Resources[HostTarget + "/authconfigs/current"] = JsonSerializer.SerializeToNode(new
        {
            id = HostTarget + "/authconfigs/current", systemData = system, properties = new
            {
                platform = new { enabled = true }, globalValidation = new { unauthenticatedClientAction = "Return401", excludedPaths = Array.Empty<string>() },
                httpSettings = new { requireHttps = true }, login = new { tokenStore = new { enabled = false } },
                identityProviders = new { azureActiveDirectory = new
                {
                    enabled = true, registration = new { clientId = config.Selection.AudienceClientId,
                        openIdIssuer = "https://login.microsoftonline.com/" + Manifest.Identity.TenantId + "/v2.0" },
                    validation = new { allowedAudiences = new[] { config.Selection.AudienceClientId, "api://" + config.Selection.AudienceClientId },
                        defaultAuthorizationPolicy = new { allowedPrincipals = new { identities = new[] { Manifest.Identity.AzureActorId, GitHub.CodePrincipalId } } } },
                } },
            },
        })!;
        var targets = new[] { StoreTarget, endpoint, endpoint + "/privatednszonegroups/default" }
            .Concat(grants.Select(g => FactoryModelsValidation.Grant(g.Item1,
                "/subscriptions/" + Manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/" + g.Item2, GitHub.CodePrincipalId, "").Target))
            .Concat([HostTarget, HostTarget + "/authconfigs/current"]).ToArray();
        var effects = targets.Select((target, i) => new FoundationOperation(1, "decide-source-operation", Manifest.Identity.FactoryId, operation, i,
            target, target.Contains("/microsoft.appconfiguration/", StringComparison.Ordinal) && !target.Contains("/roleassignments/", StringComparison.Ordinal)
                ? "2024-06-01" : target.Contains("/microsoft.network/", StringComparison.Ordinal) ? "2024-05-01"
                : target.Contains("/roleassignments/", StringComparison.Ordinal) ? "2022-04-01" : "2025-01-01",
            new string('a', 64), Guid.NewGuid(), Clock.Now, FoundationOperationState.Completed, FoundationResourceDisposition.Created)
        {
            ResourceId = target, Incarnation = created, NativeRevision = FactorySourceValidation.NativeRevision(JsonSerializer.SerializeToElement(Resources[target]), target),
            ProviderRequestId = Guid.NewGuid().ToString(), ProviderCorrelationId = operation.ToString(), ProviderOperationId = i.ToString(),
            HttpStatus = 201, DeploymentTarget = Group + "/providers/microsoft.resources/deployments/sc-" + operation.ToString("N"),
        }).ToArray();
        foreach (var effect in effects)
            Values[$"dsf/factory/source-operations/{operation:D}/{effect.Index:D4}"] = JsonSerializer.Serialize(effect, FactoryGitHubJson.Options);
        Authority = new(operation, approval, config, SourceState.Partial, "prepared", 8, effects.Select(FactoryModelsValidation.Hash).ToArray(),
            "https://source.private.azurecontainerapps.io", "Prepared source acquisition only.");
        Values[SourceKey] = JsonSerializer.Serialize(Authority, FactoryGitHubJson.Options);
        RootTree = Tree(("README.md", "100644", Blob("First party application documentation.\n")));
    }

    internal string Blob(string text) => Blob(Encoding.UTF8.GetBytes(text));
    internal string Blob(byte[] bytes)
    {
        var id = GitObject("blob", bytes);
        Blobs[id] = bytes;
        return id;
    }
    internal string Tree(params (string Path, string Mode, string Sha)[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var entry in entries.OrderBy(e => e.Path + (e.Mode == "040000" ? "/" : ""), StringComparer.Ordinal))
        {
            stream.Write(Encoding.UTF8.GetBytes(entry.Mode.TrimStart('0') + " " + entry.Path + "\0"));
            stream.Write(Convert.FromHexString(entry.Sha));
        }
        var id = GitObject("tree", stream.ToArray());
        Trees[id] = JsonSerializer.SerializeToNode(new { sha = id, truncated = false, tree = entries.Select(e => new
        {
            path = e.Path, mode = e.Mode, type = e.Mode == "040000" ? "tree" : e.Mode == "160000" ? "commit" : "blob",
            sha = e.Sha, size = Blobs.TryGetValue(e.Sha, out var bytes) ? (int?)bytes.Length : null,
        }).ToArray() })!;
        return id;
    }
    private static string GitObject(string type, byte[] bytes) =>
        Convert.ToHexStringLower(SHA1.HashData([.. Encoding.UTF8.GetBytes(type + " " + bytes.Length + "\0"), .. bytes]));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OnRequest?.Invoke(request);
        var uri = request.RequestUri!;
        Requests.Add(request.Method + " " + uri.Host + uri.PathAndQuery);
        if (uri.Host == new Uri(ProductEndpoint).Host)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(new { items = Values.Select(p => Setting(p.Key, p.Value, 1)).ToArray() });
        }
        if (uri.Host == new Uri(ProgressEndpoint).Host)
        {
            if (uri.AbsolutePath == "/kv")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                Assert.Contains(query["key"].ToString(), new[] { "source/*", "intent/*" });
                Assert.Equal("*", query["label"].ToString());
                var select = query.GetValueOrDefault("$select");
                var keysOnly = select == "key";
                Assert.True(keysOnly || select.Count == 0);
                var page = query.TryGetValue("after", out var after)
                    ? int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(after.ToString())), System.Globalization.CultureInfo.InvariantCulture) : 0;
                var keys = Progress.Keys.Where(k => k.StartsWith(query["key"].ToString()[..^1], StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
                var selected = page < EmptyProgressPages ? [] : keys.Skip((page - EmptyProgressPages) * ProgressPageSize).Take(ProgressPageSize).ToArray();
                var response = Json(new { items = selected.Select(key => keysOnly ? (object)new { key }
                    : Setting(key, Progress[key].Value, Progress[key].Revision)).ToArray() });
                if (page < EmptyProgressPages || (page - EmptyProgressPages + 1) * ProgressPageSize < keys.Length)
                {
                    var nextQuery = query.ToDictionary(p => p.Key, p => (string?)p.Value.ToString());
                    nextQuery["after"] = Convert.ToBase64String(Encoding.UTF8.GetBytes((page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    var next = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/kv", nextQuery);
                    response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
                }
                ChangeProgressPage?.Invoke(page, response);
                return response;
            }
            var key = Uri.UnescapeDataString(uri.AbsolutePath["/kv/".Length..]);
            Assert.DoesNotContain("dsf/factory", key);
            if (request.Method == HttpMethod.Get)
                return Progress.TryGetValue(key, out var value) ? Json(Setting(key, value.Value, value.Revision)) : Json(new { }, 404);
            Assert.Equal(HttpMethod.Put, request.Method);
            var content = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var text = (string)content["value"]!;
            Assert.True(Encoding.UTF8.GetByteCount(key + text) + 1024 < 10 * 1024);
            if (Progress.TryGetValue(key, out var existing))
            {
                if (request.Headers.IfNoneMatch.Count != 0 || request.Headers.IfMatch.SingleOrDefault()?.Tag != "\"" + existing.Revision + "\"")
                    return Json(new { }, 412);
            }
            else if (request.Headers.IfMatch.Count != 0) return Json(new { }, 412);
            Progress[key] = (text, ++revision);
            Writes.Add(key);
            AfterWrite?.Invoke(key, text);
            return Json(Setting(key, text, revision));
        }
        if (uri.Host == "management.azure.com")
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (Resources.TryGetValue(path, out var resource)) return Json(resource);
            if (path.Contains("/roledefinitions/", StringComparison.Ordinal))
            {
                var role = Resources.SingleOrDefault(p => p.Key.EndsWith("/roledefinitions/" + path.Split('/').Last(), StringComparison.Ordinal));
                if (role.Value is not null) return Json(role.Value);
            }
            if (path.EndsWith("/roleassignments", StringComparison.Ordinal))
                return Json(new { value = Resources.Where(r => r.Key.Contains("/roleassignments/", StringComparison.Ordinal)).Select(r => r.Value).ToArray() });
            throw new InvalidOperationException("Unexpected ARM read: " + path);
        }
        if (uri.Host == new Uri(GitHub.HostEndpoint!).Host)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/tokens/contents", uri.AbsolutePath);
            var payload = request.Headers.Authorization!.Parameter!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            BrokerClaims.Add(JsonNode.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')))!);
            return Json(new { token = InstallationToken, expires_at = Clock.Now.AddMinutes(30),
                repository_ids = new[] { 913 }, permissions = new { metadata = "read", contents = "read" } });
        }
        Assert.Equal("api.github.com", uri.Host);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(InstallationToken, request.Headers.Authorization?.Parameter);
        if (GitHubStatus != 200) return Json(new { message = "private-provider-detail" }, GitHubStatus);
        var repository = new { id = 913, full_name = Repository, owner = new { id = 81 }, default_branch = Branch, archived = false, disabled = false };
        if (uri.AbsolutePath is "/repositories/913" || uri.AbsolutePath == "/repos/" + Repository) return Json(repository);
        if (uri.AbsolutePath == "/repos/" + Repository + "/git/ref/heads/" + Uri.EscapeDataString(Branch))
            return Json(new { @ref = "refs/heads/" + Branch, @object = new { type = "commit", sha = Head } });
        if (uri.AbsolutePath == "/repos/" + Repository + "/git/commits/" + Head)
            return Json(new { sha = Head, tree = new { sha = RootTree } });
        if (uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal)) return Json(Trees[uri.Segments.Last()]);
        if (uri.AbsolutePath.Contains("/git/blobs/", StringComparison.Ordinal))
        {
            var id = uri.Segments.Last();
            var response = Blobs.TryGetValue(id, out var bytes) ? Json(new { sha = id, size = bytes.Length, encoding = "base64", content = Convert.ToBase64String(bytes) }) : Json(new { }, 404);
            AfterBlobResponse?.Invoke();
            return response;
        }
        throw new InvalidOperationException("Unexpected source request: " + uri.PathAndQuery);
    }
    private static object Setting(string key, string value, int etag) => new
        { key, value, etag = etag.ToString(), label = (string?)null,
            content_type = key.StartsWith("intent/", StringComparison.Ordinal) ? "application/vnd.dsf.intent+json;version=1" : "application/vnd.dsf.source+json;version=1", locked = false };
    internal static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status)
        { Content = JsonContent.Create(value) };
}

internal sealed class SourceTestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-09-16T03:00:00Z");
    private readonly List<ClockTimer> timers = [];
    internal DateTimeOffset Now
    {
        get => now;
        set { now = value; foreach (var timer in timers.ToArray()) timer.Fire(); }
    }
    public override DateTimeOffset GetUtcNow() => Now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    private sealed class ClockTimer(SourceTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? due;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            return true;
        }
        internal void Fire() { if (due <= clock.Now) { due = null; callback(state); } }
        public void Dispose() => clock.timers.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class SourceTestCredential(SourceInventoryTransport transport) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext request, CancellationToken token)
    {
        var resource = request.Scopes.Single()[..^"/.default".Length];
        var v2 = transport.V2Tokens && resource.StartsWith("api://", StringComparison.Ordinal);
        var value = JsonSerializer.SerializeToNode(new
        {
            oid = transport.GitHub.CodePrincipalId, tid = transport.Manifest.Identity.TenantId,
            appid = transport.GitHub.CodeClientId, xms_mirid = transport.Code.Target,
            ver = v2 ? "2.0" : "1.0",
            aud = v2 ? resource["api://".Length..] : resource, nbf = transport.Clock.Now.AddMinutes(-1).ToUnixTimeSeconds(),
            exp = transport.Clock.Now.AddHours(1).ToUnixTimeSeconds(),
        })!;
        if (v2) { value["azp"] = value["appid"]!.DeepClone(); value.AsObject().Remove("appid"); }
        if (resource.StartsWith("api://", StringComparison.Ordinal)) transport.ChangeApiToken?.Invoke(value.AsObject());
        var claims = JsonSerializer.SerializeToUtf8Bytes(value);
        return new("test." + Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".unsigned",
            transport.Clock.Now.AddHours(1));
    }
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken token) => ValueTask.FromResult(GetToken(request, token));
}

internal sealed class SourceHostRunner(SourceInventoryTransport transport) : IWebHostRunner
{
    internal string Body = "";
    internal HttpStatusCode Status;
    internal string Route = "/acquire";
    internal object Request = new SourceAcquireRequest();
    internal bool CodeCaller { get; set; }
    public async Task RunAsync(WebApplication app, CancellationToken token)
    {
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync(token);
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var principal = CodeCaller ? transport.GitHub.CodePrincipalId : transport.Manifest.Identity.AzureActorId;
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", principal);
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_typ = "aad", claims = new[] { new { typ = "oid", val = principal },
                    new { typ = "tid", val = transport.Manifest.Identity.TenantId }, new { typ = "appid", val = transport.GitHub.CodeClientId } },
            })));
            using var response = await client.PostAsJsonAsync(Route, Request, FactoryGitHubJson.Options, token);
            Status = response.StatusCode;
            Body = await response.Content.ReadAsStringAsync(token);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
