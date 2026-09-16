using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactoryTelemetryHostTests
{
    [Fact]
    public async Task Labelled_configuration_is_not_unlabelled_product_authority()
    {
        using var transport = new TelemetryTransport { Label = "foreign-environment" };
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), TestDependencies.Build(webHostRunner: runner) with
            {
                TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport), TelemetryClock = new TelemetryClock(),
            }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Response);
        Assert.Empty(transport.Queries);
    }

    [Theory]
    [InlineData("valid", "Ready", 2)]
    [InlineData("second-empty", "Blocked", 3)]
    public async Task Every_selected_backend_is_queried_and_one_backends_record_cannot_hide_another_empty_source(
        string scenario, string state, int queries)
    {
        using var transport = new TelemetryTransport(multipleBackends: true) { Scenario = scenario };
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), TestDependencies.Build(webHostRunner: runner) with
            {
                TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport), TelemetryClock = new TelemetryClock(),
            }, CancellationToken.None);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal(state, (string?)proof["state"]);
        Assert.Equal(queries, transport.Queries.Count);
        Assert.Equal(2, proof["queries"]!.AsArray().Count);
        Assert.Contains(TelemetryTransport.Workspace + "-second", proof["queries"]!.ToJsonString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1800, true)]
    public async Task Actual_host_enforces_reviewed_positive_windows_in_the_native_query_range(int seconds, bool ready)
    {
        using var transport = new TelemetryTransport(seconds);
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), TestDependencies.Build(webHostRunner: runner) with
            {
                TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport), TelemetryClock = new TelemetryClock(),
            }, CancellationToken.None);
        Assert.Equal(ready ? HttpStatusCode.OK : HttpStatusCode.Forbidden, runner.Response);
        if (!ready) Assert.Empty(transport.Queries);
        else
        {
            Assert.Equal("2026-09-16T11:30:00.0000000Z/2026-09-16T12:00:00.0000000Z", (string?)transport.Queries.Single()["timespan"]);
            Assert.Equal("2026-09-16T11:30:00+00:00", (string?)JsonNode.Parse(runner.Body)!["records"]![0]!["recordTime"]);
        }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("environment")]
    [InlineData("query")]
    [InlineData("receipt")]
    public async Task Authority_changed_during_query_never_releases_stale_proof(string change)
    {
        using var transport = new TelemetryTransport();
        transport.OnQuery = () =>
        {
            var current = JsonNode.Parse(transport.Values[ProductConfigurationKeys.FactoryTelemetry])!;
            if (change == "identity") current["configuration"]!["sourceIdentity"]!["principalId"] = Guid.NewGuid().ToString();
            if (change == "environment") current["configuration"]!["applicationEnvironment"] = "staging";
            if (change == "query") current["configuration"]!["queries"]!["AppRequests"] = "workspace('other').AppRequests";
            if (change == "receipt") transport.Values.Remove(ProductConfigurationKeys.TelemetryOperation(transport.Authority.OperationId, 0));
            else transport.Values[ProductConfigurationKeys.FactoryTelemetry] = current.ToJsonString();
        };
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), TestDependencies.Build(webHostRunner: runner) with
            {
                TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport), TelemetryClock = new TelemetryClock(),
            }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Response);
        Assert.Single(transport.Queries);
        Assert.DoesNotContain("record-123", runner.Body);
    }

    [Fact]
    public async Task Hosted_proof_requires_complete_current_grant_receipts_not_just_configured_identity()
    {
        using var transport = new TelemetryTransport();
        transport.Values.Remove(ProductConfigurationKeys.TelemetryOperation(transport.Authority.OperationId, 0));
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), TestDependencies.Build(webHostRunner: runner) with
            {
                TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport), TelemetryClock = new TelemetryClock(),
            }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Response);
        Assert.Empty(transport.Queries);
    }

    [Theory]
    [InlineData("empty", "Empty")]
    [InlineData("stale", "Stale")]
    [InlineData("partial", "PartialResponse")]
    [InlineData("wrong-scope", "WrongScope")]
    [InlineData("malformed", "MalformedResponse")]
    [InlineData("permission", "PermissionDenied")]
    [InlineData("network", "NetworkUnavailable")]
    [InlineData("wrong-workspace", "WrongScope")]
    [InlineData("wrong-table", "WrongScope")]
    [InlineData("wrong-identity", "AmbiguousAuthority")]
    [InlineData("before-boundary", "WrongScope")]
    [InlineData("after-window", "WrongScope")]
    [InlineData("missing-id", "MalformedResponse")]
    [InlineData("ragged-row", "MalformedResponse")]
    public async Task Actual_query_failures_are_distinct_not_ready_without_health_diagnosis(string scenario, string reason)
    {
        using var transport = new TelemetryTransport { Scenario = scenario };
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        var deps = TestDependencies.Build(webHostRunner: runner) with
        {
            TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport), TelemetryClock = new TelemetryClock(),
        };
        await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), deps, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, runner.Response);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.NotEqual("Ready", (string?)proof["state"]);
        Assert.Equal(reason, (string?)proof["reason"]);
        Assert.DoesNotContain("secret-provider-detail", runner.Body);
        Assert.Contains("NOT READY", runner.Body);
    }

    [Fact]
    public async Task One_native_application_record_at_the_exact_freshness_boundary_proves_the_actual_source_path()
    {
        using var transport = new TelemetryTransport();
        using var http = new HttpClient(transport);
        var runner = new TelemetryRunner(transport);
        var deps = TestDependencies.Build(webHostRunner: runner) with
        {
            TelemetryHttp = http, TelemetryCredential = new TelemetryCredential(transport),
            TelemetryClock = new TelemetryClock(),
        };

        var exit = await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], transport.Environment,
            new StringWriter(), new StringWriter(), deps, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.OK, runner.Response);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("Ready", (string?)proof["state"]);
        Assert.Equal("2026-09-15T12:00:00+00:00", (string?)proof["windowStart"]);
        Assert.Equal("2026-09-16T12:00:00+00:00", (string?)proof["windowEnd"]);
        Assert.Equal(TelemetryTransport.Component, (string?)proof["records"]![0]!["applicationResourceId"]);
        Assert.Equal("record-123", (string?)proof["records"]![0]!["itemId"]);
        Assert.Equal("2026-09-15T12:00:00+00:00", (string?)proof["records"]![0]!["recordTime"]);
        Assert.Single(transport.Queries);
        Assert.Equal("AppRequests | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type", (string?)transport.Queries[0]["query"]);
        Assert.Equal("2026-09-15T12:00:00.0000000Z/2026-09-16T12:00:00.0000000Z", (string?)transport.Queries[0]["timespan"]);
        Assert.Equal(2, transport.ConfigurationReads);
    }
    [Fact]
    public async Task Installed_runtime_hosts_native_telemetry_proof_instead_of_running_the_conveyor()
    {
        var runner = new TelemetryRunner();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var dependencies = TestDependencies.Build(webHostRunner: runner);

        var exit = await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "widget", ["AZURE_APPCONFIG_ENDPOINT"] = "https://product.azconfig.io",
            ["DSF_FACTORY_PROFILE_PIN"] = new string('a', 64), ["AZURE_CLIENT_ID"] = Guid.NewGuid().ToString(),
        }, stdout, stderr, dependencies, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Response);
        Assert.Contains("product-governance", runner.Body);
    }

    private sealed class TelemetryRunner(TelemetryTransport? transport = null) : IWebHostRunner
    {
        public HttpStatusCode? Response { get; private set; }
        public string Body { get; private set; } = "";
        public async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
        {
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync(cancellationToken);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                if (transport is not null)
                {
                    var identity = transport.Manifest.Identity;
                    client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
                    client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", identity.AzureActorId);
                    client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        auth_typ = "aad", claims = new[]
                        {
                            new { typ = "oid", val = identity.AzureActorId }, new { typ = "tid", val = identity.TenantId },
                        },
                    })));
                }
                using var response = await client.PostAsync("/proof", null, cancellationToken);
                Response = response.StatusCode;
                Body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            finally { await app.StopAsync(cancellationToken); }
        }
    }

    internal sealed class TelemetryClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T12:00:00Z");
    }

    internal sealed class TelemetryCredential(TelemetryTransport transport) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var source = transport.Authority.Configuration.SourceIdentity;
            var claims = new { oid = transport.Scenario == "wrong-identity" ? "ffffffff-ffff-ffff-ffff-ffffffffffff" : source.PrincipalId, tid = transport.Manifest.Identity.TenantId,
                appid = source.ClientId, xms_mirid = source.ResourceId };
            return ValueTask.FromResult(new AccessToken("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    internal sealed class TelemetryTransport : HttpMessageHandler
    {
        internal const string Component = "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/app-a/providers/microsoft.insights/components/application";
        internal const string Workspace = "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/logs/providers/microsoft.operationalinsights/workspaces/app";
        internal const string WorkspaceId = "44444444-4444-4444-4444-444444444444";
        public Dictionary<string, string> Values { get; }
        public FactoryManifest Manifest { get; }
        public FactoryTelemetry Authority { get; }
        public Dictionary<string, string?> Environment { get; }
        public List<JsonNode> Queries { get; } = [];
        public int ConfigurationReads { get; private set; }
        public string Scenario { get; init; } = "valid";
        public Action? OnQuery { get; set; }
        public string? Label { get; init; }
        private readonly int freshnessSeconds;

        public TelemetryTransport(int freshnessSeconds = 86400, bool multipleBackends = false)
        {
            this.freshnessSeconds = freshnessSeconds;
            Values = AuthorityTransport.ReadFixture();
            var original = JsonSerializer.Deserialize<FactoryManifest>(Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
            var source = Binding("source");
            var hosting = Binding("hosting");
            var endpoint = "https://dsfo-" + original.Identity.FactoryId.ToString("N") + ".azconfig.io";
            var backends = new List<TelemetryBackend> { new(Workspace, WorkspaceId, ["AppRequests"], [Component]) };
            if (multipleBackends) backends.Add(new(Workspace + "-second", "55555555-5555-5555-5555-555555555555", ["AppRequests"], [Component + "-second"]));
            var config = new FactoryTelemetryConfiguration(1, original.Identity.FactoryId, original.Identity.PlanFingerprint,
                original.Identity.ApplicationEnvironment, backends.SelectMany(b => b.ApplicationResourceIds).ToArray(),
                backends, source, hosting, endpoint,
                "ghcr.io/dsf/runtime@sha256:" + new string('a', 64), "77777777-7777-7777-7777-777777777777",
                freshnessSeconds, FactoryTelemetryValidation.QueryRevision);
            config = config with { AccessGrants = FactoryTelemetryValidation.Grants(config, original.Identity.SubscriptionId) };
            var operationId = Guid.NewGuid();
            var host = hosting.Target[..hosting.Target.IndexOf("/providers/", StringComparison.Ordinal)]
                + "/providers/microsoft.app/containerapps/df-" + original.Identity.FactoryId.ToString("N")[..14] + "-telemetry";
            var operations = config.AccessGrants.Select((g, i) => source with
            {
                Kind = "decide-telemetry-operation", OperationId = operationId, Index = i, Target = g.Target, ResourceId = g.Target,
                ApiVersion = "2022-04-01", RoleScope = g.Scope, RoleDefinitionId = g.RoleDefinitionId,
                PrincipalId = g.PrincipalId, ClientId = null, HttpStatus = 201,
                ProviderRequestId = Guid.NewGuid().ToString(), ProviderCorrelationId = Guid.NewGuid().ToString(),
                Incarnation = "2026-09-16T11:00:00.0000000+00:00",
            }).Concat(new[] { host, host + "/authconfigs/current" }.Select((target, i) => source with
            {
                Kind = "decide-telemetry-operation", OperationId = operationId, Index = config.AccessGrants.Count + i,
                Target = target, ResourceId = target, ApiVersion = "2025-01-01", PrincipalId = null, ClientId = null,
                LogicalIdentity = null, HttpStatus = 201, ProviderRequestId = Guid.NewGuid().ToString(), ProviderCorrelationId = Guid.NewGuid().ToString(),
                Incarnation = "2026-09-16T11:00:00.0000000+00:00",
            })).ToArray();
            foreach (var operation in operations)
                Values[ProductConfigurationKeys.TelemetryOperation(operationId, operation.Index)] = JsonSerializer.Serialize(operation, FactoryGitHubJson.Options);
            Authority = new(1, operationId, FactoryTelemetryValidation.Hash(config), config, TelemetryState.Partial, "checking")
            {
                CheckId = Guid.NewGuid(), OperationCount = operations.Length,
                EffectHashes = operations.ToDictionary(o => o.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), FactoryTelemetryValidation.Hash),
            };
            Manifest = original with { TelemetryOperationId = Authority.OperationId, TelemetryApprovalFingerprint = Authority.ApprovalFingerprint };
            Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(Manifest, FactoryGitHubJson.Options);
            Values[ProductConfigurationKeys.FactoryTelemetry] = JsonSerializer.Serialize(Authority, FactoryGitHubJson.Options);
            Environment = new()
            {
                ["DSF_PRODUCT"] = original.Identity.ProductKey, ["AZURE_APPCONFIG_ENDPOINT"] = endpoint,
                ["DSF_FACTORY_PROFILE_PIN"] = original.ProfilePin, ["AZURE_CLIENT_ID"] = source.ClientId,
            };

            FoundationOperation Binding(string name) => JsonSerializer.Deserialize<FoundationOperation>(
                Values.Single(p => p.Key.Contains("/github-bindings/", StringComparison.Ordinal)
                    && p.Key.EndsWith("/" + name, StringComparison.Ordinal)).Value, FactoryGitHubJson.Options)!;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Headers.Authorization);
            if (request.RequestUri!.Host.EndsWith(".azconfig.io", StringComparison.Ordinal))
            {
                ConfigurationReads++;
                return Reply(new { items = Values.Select(p => new { key = p.Key, value = p.Value, label = Label, etag = "revision" }) });
            }
            Assert.Equal("api.loganalytics.io", request.RequestUri.Host);
            var backend = Authority.Configuration.Backends.Single(b => request.RequestUri.AbsolutePath == "/v1/workspaces/" + b.WorkspaceId + "/query");
            Assert.Equal(HttpMethod.Post, request.Method);
            Queries.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            OnQuery?.Invoke();
            if (Scenario == "network") throw new HttpRequestException("secret-provider-detail");
            if (Scenario == "permission") return new(HttpStatusCode.Forbidden)
                { Content = new StringContent("""{"error":{"code":"InsufficientAccessError","message":"secret-provider-detail"}}""") };
            if (Scenario == "partial") return Reply(new { error = new { code = "PartialError", message = "secret-provider-detail",
                details = new[] { new { code = "QueryPartialError", message = "secret-provider-detail" } } }, tables = Array.Empty<object>() });
            var staleProbe = Queries.Count > 1;
            var rows = Scenario == "empty" || Scenario == "stale" && !staleProbe
                || Scenario == "second-empty" && backend.WorkspaceId != WorkspaceId
                ? Array.Empty<string[]>()
                : new[] { new[] { Scenario is "stale" or "before-boundary" ? "2026-09-15T11:59:59.9999999Z"
                    : Scenario == "after-window" ? "2026-09-16T12:00:00.0000001Z"
                    : freshnessSeconds == 1800 ? "2026-09-16T11:30:00Z" : "2026-09-15T12:00:00Z",
                    Scenario == "wrong-scope" ? Component + "-other" : backend.ApplicationResourceIds[0], Scenario == "missing-id" ? "" : "record-123",
                    Scenario == "wrong-workspace" ? "ffffffff-ffff-ffff-ffff-ffffffffffff" : backend.WorkspaceId,
                    Scenario == "wrong-table" ? "AzureActivity" : "AppRequests" } };
        if (Scenario == "ragged-row") rows[0] = [.. rows[0], "secret-provider-detail"];
            return Reply(new { tables = new[] { new
            {
                name = "PrimaryResult",
                columns = new[] { new { name = "TimeGenerated", type = Scenario == "malformed" ? "string" : "datetime" }, new { name = "_ResourceId", type = "string" },
                    new { name = "_ItemId", type = "string" }, new { name = "TenantId", type = "string" },
                    new { name = "Type", type = "string" } },
                rows,
            } } });
        }

        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
