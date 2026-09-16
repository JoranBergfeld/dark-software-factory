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

public sealed class FactoryModelsHostTests
{
    [Fact]
    public async Task Slow_topology_cannot_turn_old_route_observations_into_fresh_capability()
    {
        using var transport = new ModelHostTransport();
        transport.OnInference = () => transport.Clock.Now = transport.Clock.Now.AddMinutes(2);
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = transport.Clock }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        Assert.Equal("Expired", (string?)JsonNode.Parse(runner.Body)!["observations"]![0]!["reason"]);
    }

    [Theory]
    [InlineData("missing-juror", "juror-2", "MissingDeployment")]
    [InlineData("mislabeled-juror", "juror-2", "IdentityMismatch")]
    [InlineData("malformed-judgment", "juror-2", "MalformedResponse")]
    [InlineData("denied-inference", "juror-2", "PermissionDenied")]
    [InlineData("network", "juror-2", "NetworkUnavailable")]
    [InlineData("timeout", "juror-2", "NetworkUnavailable")]
    [InlineData("expired-token", "synthesis", "IdentityMismatch")]
    [InlineData("wrong-token", "synthesis", "IdentityMismatch")]
    [InlineData("wrong-token-audience", "synthesis", "IdentityMismatch")]
    [InlineData("wrong-token-version", "synthesis", "IdentityMismatch")]
    [InlineData("truncated", "synthesis", "MalformedResponse")]
    [InlineData("embedding-dimensions", "embedding", "MalformedResponse")]
    [InlineData("embedding-empty", "embedding", "MalformedResponse")]
    [InlineData("embedding-zero", "embedding", "MalformedResponse")]
    [InlineData("embedding-indices", "embedding", "MalformedResponse")]
    [InlineData("wrong-response-model", "juror-2", "IdentityMismatch")]
    [InlineData("replaced-grant", "juror-2", "IdentityMismatch")]
    public async Task Required_route_failures_are_precise_blockers_never_fallbacks(string scenario, string route, string reason)
    {
        using var transport = new ModelHostTransport { Scenario = scenario };
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = new ModelClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("Blocked", (string?)proof["state"]);
        var observation = proof["observations"]!.AsArray().Single(o => (string?)o!["route"] == route)!;
        Assert.Equal(reason, (string?)observation["reason"]);
        Assert.DoesNotContain("unrelated-secret", runner.Body);
        Assert.Equal(5, proof["observations"]!.AsArray().Count);
    }

    [Fact]
    public async Task Changed_configuration_after_inference_releases_no_stale_proof()
    {
        using var transport = new ModelHostTransport();
        transport.OnInference = () => transport.Values.Remove(ProductConfigurationKeys.FactoryProfile);
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = new ModelClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.DoesNotContain("outputSha256", runner.Body);
    }

    [Fact]
    public async Task Native_role_expansion_after_inference_cannot_be_treated_as_approved_effective_access()
    {
        using var transport = new ModelHostTransport();
        transport.OnInference = () =>
        {
            var role = transport.Resources.Single(p => p.Key.Contains("/roledefinitions/", StringComparison.Ordinal)).Value;
            role["properties"]!["permissions"]![0]!["dataActions"]!.AsArray()
                .Add("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read");
        };
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = transport.Clock }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        Assert.Equal("UnsupportedTarget", (string?)JsonNode.Parse(runner.Body)!["observations"]![0]!["reason"]);
        Assert.Single(transport.Inference);
    }

    [Fact]
    public async Task Prepared_stage_with_no_effects_is_not_workload_authority()
    {
        using var transport = new ModelHostTransport();
        var authority = JsonNode.Parse(transport.Values[ProductConfigurationKeys.FactoryModels])!;
        authority["operationCount"] = 0;
        authority["effectHashes"] = new JsonArray();
        transport.Values[ProductConfigurationKeys.FactoryModels] = authority.ToJsonString();
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = new ModelClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Empty(transport.Inference);
    }

    [Fact]
    public async Task No_proof_after_three_operator_aliases_for_one_native_family()
    {
        using var transport = new ModelHostTransport();
        var authority = JsonNode.Parse(transport.Values[ProductConfigurationKeys.FactoryModels])!;
        for (var i = 2; i < 5; i++)
        {
            var key = ProductConfigurationKeys.ModelRoute(transport.Authority.OperationId, i);
            var route = JsonNode.Parse(transport.Values[key])!;
            route["selection"]!["format"] = "OpenAI";
            route["selection"]!["family"] = "gpt";
            route["selection"]!["model"] = "gpt-4.1";
            transport.Values[key] = route.ToJsonString();
            authority["configuration"]!["routeHashes"]![i] = FactoryModelsValidation.Hash(
                JsonSerializer.Deserialize<ReviewedModelRoute>(route.ToJsonString(), FactoryGitHubJson.Options)!);
        }
        var config = JsonSerializer.Deserialize<FactoryModelsConfiguration>(authority["configuration"]!.ToJsonString(), FactoryGitHubJson.Options)!;
        authority["approvalFingerprint"] = FactoryModelsValidation.Hash(config);
        transport.Values[ProductConfigurationKeys.FactoryModels] = authority.ToJsonString();
        var manifest = transport.Manifest with { ModelsApprovalFingerprint = FactoryModelsValidation.Hash(config) };
        transport.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(manifest, FactoryGitHubJson.Options);
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = new ModelClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Empty(transport.Inference);
    }

    [Theory]
    [InlineData("deduplicate")]
    [InlineData("synthesis")]
    [InlineData("jurors/juror-2")]
    public async Task Verified_consumers_use_the_same_model_routes_and_retain_caller_problem_ids(string route)
    {
        using var transport = new ModelHostTransport();
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport) { Consume = true, ConsumerRoute = route };
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            {
                ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = new ModelClock(),
            }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, runner.ConsumerStatus);
        var result = JsonNode.Parse(runner.ConsumerBody)!;
        if (route == "deduplicate") Assert.Equal(new[] { "existing-problem", "new-observation" },
            result["groups"]![0]!.AsArray().Select(n => (string)n!));
        else Assert.NotEmpty((string)result["text"]!);
        Assert.Equal(6, transport.Inference.Count);
        Assert.Equal(transport.Authority.ApprovalFingerprint, (string?)result["observation"]!["configurationRevision"]);
    }

    [Fact]
    public async Task Largest_supported_embedding_batch_is_deduplicated_without_truncation_or_response_budget_failure()
    {
        using var transport = new ModelHostTransport(3072);
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport)
        {
            Consume = true,
            BeforeConsume = request => request["items"] = new JsonArray(Enumerable.Range(0, 32)
                .Select(i => JsonSerializer.SerializeToNode(new ModelText("problem-" + i, "Nonsensitive bounded grouping input."),
                    FactoryGitHubJson.Options)).ToArray()),
        };
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = transport.Clock }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, runner.ConsumerStatus);
        var result = JsonNode.Parse(runner.ConsumerBody)!;
        Assert.Equal(32, result["groups"]!.AsArray().SelectMany(g => g!.AsArray()).Count());
        Assert.Equal(3072, (int?)result["observation"]!["dimensions"]);
        Assert.Equal(32, transport.Inference.Last()["input"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("null-items", 403)]
    [InlineData("null-item", 409)]
    [InlineData("null-capability", 403)]
    [InlineData("expired", 403)]
    [InlineData("stale-reference", 403)]
    [InlineData("changed-during-inference", 403)]
    public async Task Consumers_reject_missing_input_or_stale_authority_without_releasing_results(string scenario, int status)
    {
        using var transport = new ModelHostTransport();
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport)
        {
            Consume = true,
            BeforeConsume = request =>
            {
                if (scenario == "null-items") request["items"] = null;
                if (scenario == "null-item") request["items"]![0] = null;
                if (scenario == "null-capability") request["capability"] = null;
                if (scenario == "expired") transport.Clock.Now = transport.Clock.Now.AddDays(1);
                if (scenario == "stale-reference") request["capability"]!["configurationRevision"] = new string('f', 64);
                if (scenario == "changed-during-inference")
                    transport.OnInference = () => transport.Values.Remove(ProductConfigurationKeys.FactoryProfile);
            },
        };
        await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), new StringWriter(),
            TestDependencies.Build(webHostRunner: runner) with
            { ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = transport.Clock }, CancellationToken.None);
        Assert.Equal((HttpStatusCode)status, runner.ConsumerStatus);
        Assert.Equal("Blocked", (string?)JsonNode.Parse(runner.ConsumerBody)!["state"]);
        Assert.DoesNotContain("existing-problem", runner.ConsumerBody);
        Assert.Equal(scenario == "changed-during-inference" ? 6 : 5, transport.Inference.Count);
    }

    [Fact]
    public async Task Actual_host_runs_synthesis_semantic_dedup_and_each_distinct_juror_as_the_bound_workload()
    {
        using var transport = new ModelHostTransport();
        using var http = new HttpClient(transport);
        var runner = new ModelHostRunner(transport);
        var error = new StringWriter();
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-models"], transport.Environment, new StringWriter(), error,
            TestDependencies.Build(webHostRunner: runner) with
            {
                ModelHttp = http, ModelCredential = new ModelCredential(transport), ModelClock = new ModelClock(),
            }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.OK, runner.Status);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("Verified", (string?)proof["state"]);
        Assert.Equal(5, proof["observations"]!.AsArray().Count);
        Assert.Equal(5, transport.Inference.Count);
        Assert.Equal(new[] { "one", "two" }, proof["observations"]![1]!["groups"]![0]!.AsArray().Select(n => (string)n!));
        Assert.Equal(new[] { "three" }, proof["observations"]![1]!["groups"]![1]!.AsArray().Select(n => (string)n!));
        Assert.All(transport.Scopes, scope => Assert.Contains(scope, new[] { "https://azconfig.io/.default",
            "https://management.azure.com/.default", "https://ai.azure.com/.default" }));
        Assert.DoesNotContain("CAPABILITY PROBE", runner.Body);
        Assert.DoesNotContain("unrelated-secret", runner.Body + error);
    }

    internal sealed class ModelClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class ModelHostRunner(ModelHostTransport transport) : IWebHostRunner
    {
        public HttpStatusCode Status { get; private set; }
        public string Body { get; private set; } = "";
        public bool Consume { get; init; }
        public string ConsumerRoute { get; init; } = "deduplicate";
        public HttpStatusCode ConsumerStatus { get; private set; }
        public string ConsumerBody { get; private set; } = "";
        public Action<JsonNode>? BeforeConsume { get; init; }
        public async Task RunAsync(WebApplication app, CancellationToken token)
        {
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync(token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", transport.Manifest.Identity.AzureActorId);
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    auth_typ = "aad", claims = new[] { new { typ = "oid", val = transport.Manifest.Identity.AzureActorId },
                        new { typ = "tid", val = transport.Manifest.Identity.TenantId } },
                })));
                using var response = await client.PostAsync("/proof", null, token);
                Status = response.StatusCode;
                Body = await response.Content.ReadAsStringAsync(token);
                if (Consume)
                {
                    var proof = JsonSerializer.Deserialize<ModelCapabilityProof>(Body, FactoryGitHubJson.Options)!;
                    var proofKey = ProductConfigurationKeys.ModelProof(proof.OperationId, proof.CheckId);
                    transport.Values[proofKey] = Body;
                    var stored = JsonSerializer.Deserialize<FactoryModels>(transport.Values[ProductConfigurationKeys.FactoryModels], FactoryGitHubJson.Options)!;
                    stored = stored with { State = ModelCapabilityState.Verified, Stage = "observed", LastProofReference = proofKey,
                        LastProofSha256 = FactoryModelsValidation.Hash(proof), VerifiedUntil = proof.ExpiresAt };
                    transport.Values[ProductConfigurationKeys.FactoryModels] = JsonSerializer.Serialize(stored, FactoryGitHubJson.Options);
                    var code = transport.Authority.Bindings![ConsumerRoute.StartsWith("jurors/", StringComparison.Ordinal) ? 0 : 1];
                    client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL-ID");
                    client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL");
                    client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", code.PrincipalId);
                    client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        auth_typ = "aad", claims = new[] { new { typ = "oid", val = code.PrincipalId! },
                            new { typ = "appid", val = code.ClientId! }, new { typ = "tid", val = transport.Manifest.Identity.TenantId } },
                    })));
                    var reference = new ModelCapabilityReference(proof.OperationId, proof.ConfigurationRevision, proofKey, stored.LastProofSha256!);
                    object request = ConsumerRoute == "deduplicate" ? new ModelDeduplicationRequest(reference,
                        [new("existing-problem", "Sessions expire and prevent login."), new("new-observation", "Expired sessions block sign in."),
                            new("different-problem", "Invoices need export to PDF.")])
                        : new ModelWorkloadRequest(reference, "Explain why equivalent problem descriptions should retain a stable identifier.");
                    var requestBody = JsonSerializer.SerializeToNode(request, FactoryGitHubJson.Options)!;
                    BeforeConsume?.Invoke(requestBody);
                    using var consumed = await client.PostAsync("/" + ConsumerRoute,
                        new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json"), token);
                    ConsumerStatus = consumed.StatusCode;
                    ConsumerBody = await consumed.Content.ReadAsStringAsync(token);
                }
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
    }

    internal sealed class ModelCredential(ModelHostTransport transport) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => GetTokenAsync(context, token).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
        {
            transport.Scopes.Add(context.Scopes.Single());
            var binding = transport.Authority.Bindings![0];
            var now = transport.Clock.GetUtcNow();
            var claims = new
            {
                oid = transport.Scenario == "wrong-token" ? "11111111-ffff-ffff-ffff-ffffffffffff" : binding.PrincipalId,
                appid = binding.ClientId, tid = transport.Manifest.Identity.TenantId,
                ver = transport.Scenario == "wrong-token-version" ? "2.0" : "1.0",
                xms_mirid = binding.Target, exp = (transport.Scenario == "expired-token" ? now.AddSeconds(-1) : now.AddHours(1)).ToUnixTimeSeconds(),
                nbf = now.AddMinutes(-1).ToUnixTimeSeconds(),
                aud = transport.Scenario == "wrong-token-audience" ? binding.ClientId : context.Scopes.Single()[..^"/.default".Length],
            };
            return ValueTask.FromResult(new AccessToken("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test", now.AddHours(1)));
        }
    }

    internal sealed class ModelHostTransport : HttpMessageHandler
    {
        public Dictionary<string, string> Values { get; }
        public Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public FactoryManifest Manifest { get; }
        public FactoryModels Authority { get; }
        public Dictionary<string, string?> Environment { get; }
        public List<string> Scopes { get; } = [];
        public List<JsonNode> Inference { get; } = [];
        public ModelClock Clock { get; } = new();
        public string Scenario { get; set; } = "";
        public Action? OnInference { get; set; }
        public Func<string, string>? Completion { get; set; }
        private const string Account = "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/models/providers/microsoft.cognitiveservices/accounts/models";
        private const string Endpoint = "https://models.services.ai.azure.com";

        public ModelHostTransport(int dimensions = 1536)
        {
            Values = AuthorityTransport.ReadFixture();
            var original = JsonSerializer.Deserialize<FactoryManifest>(Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
            var source = Binding("source");
            var prefix = "df-" + original.Identity.FactoryId.ToString("N")[..14];
            var model = source with { LogicalIdentity = "factory:council", Target = source.Target.Replace("-source", "-council", StringComparison.Ordinal),
                ResourceId = source.Target.Replace("-source", "-council", StringComparison.Ordinal), PrincipalId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" };
            var bindings = new[] { model, Binding("code"), Binding("hosting") };
            var role = "/subscriptions/" + original.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/cccccccc-cccc-cccc-cccc-cccccccccccc";
            var routes = new[] {
                Route("synthesis", "OpenAI", "gpt", "gpt-4.1", "2025-04-14", true, 0),
                Route("embedding", "OpenAI", "text-embedding", dimensions == 1536 ? "text-embedding-3-small" : "text-embedding-3-large", "1", true, dimensions),
                Route("juror-1", "OpenAI", "gpt", "gpt-4.1", "2025-04-14", false, 0),
                Route("juror-2", "DeepSeek", "deepseek", "DeepSeek-V3-0324", "1", false, 0),
                Route("juror-3", "xAI", "grok", "grok-3", "1", false, 0),
            };
            var scope = model.Target[..model.Target.IndexOf("/providers/", StringComparison.Ordinal)]
                + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + original.Identity.FactoryId.ToString("N");
            var nativeRole = JsonSerializer.SerializeToNode(new
            {
                id = role, properties = new { type = "CustomRole", permissions = new[] { new
                {
                    actions = new[] { "Microsoft.CognitiveServices/accounts/read", "Microsoft.CognitiveServices/accounts/deployments/read",
                        "Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read" },
                    dataActions = new[] { "Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action",
                        "Microsoft.CognitiveServices/accounts/OpenAI/deployments/embeddings/action",
                        "Microsoft.CognitiveServices/accounts/MaaS/chat/completions/action" },
                    notActions = Array.Empty<string>(), notDataActions = Array.Empty<string>(),
                } } },
            })!;
            Resources[Account + "/providers/microsoft.authorization/roledefinitions/" + role.Split('/').Last()] = nativeRole;
            var grants = new[] {
                FactoryModelsValidation.Grant(scope, "/subscriptions/" + original.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/" + FactoryModelsValidation.ConfigReaderRole, model.PrincipalId!, new string('a', 64)),
                FactoryModelsValidation.Grant(Account, role, model.PrincipalId!, FactoryModelsValidation.Hash(Element(nativeRole).GetProperty("properties"))),
            };
            var config = new FactoryModelsConfiguration(1, original.Identity.FactoryId, original.Identity.PlanFingerprint,
                "https://dsfo-" + original.Identity.FactoryId.ToString("N") + ".azconfig.io", original.ProfilePin,
                "ghcr.io/dsf/runtime@sha256:" + new string('a', 64), "77777777-7777-7777-7777-777777777777", 3600,
                routes.Select(FactoryModelsValidation.Hash).ToArray(), bindings.Select(FactoryModelsValidation.Hash).ToArray())
                { GrantHashes = grants.Select(FactoryModelsValidation.Hash).ToArray(), HostTemplateSha256 = new string('c', 64) };
            var authority = new FactoryModels(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), FactoryModelsValidation.Hash(config), config, ModelCapabilityState.Partial, "checking")
                { Routes = routes, Bindings = bindings, Grants = grants };
            var host = FactoryModelsValidation.HostTarget(authority);
            var targets = routes.Take(2).Select(r => FactoryModelsValidation.DeploymentTarget(r.Selection)).Concat(grants.Select(g => g.Target))
                .Concat([host, host + "/authconfigs/current"]).ToArray();
            var effects = targets.Select((target, i) =>
            {
                var grant = i is 2 or 3 ? grants[i - 2] : null;
                var incarnation = "2026-09-16T11:00:00.0000000+00:00";
                var revision = i < 2 ? FactoryModelsValidation.ObserveDeployment(routes[i].Selection, Element(Resources[target])).Revision : new string('d', 64);
                if (grant is not null)
                {
                    Resources[target] = JsonSerializer.SerializeToNode(new { id = target, properties = new
                    {
                        principalId = grant.PrincipalId, principalType = "ServicePrincipal", roleDefinitionId = grant.RoleDefinitionId,
                        scope = grant.Scope, createdBy = original.Identity.AzureActorId, createdOn = incarnation, condition = (string?)null,
                    } })!;
                    revision = FactoryModelsValidation.Hash(Element(Resources[target]) .GetProperty("properties"));
                }
                return model with
                {
                    Kind = FactoryModelsValidation.OperationKind, OperationId = authority.OperationId, Index = i, Target = target, ResourceId = target,
                    ApiVersion = i < 2 ? FactoryModelsValidation.ProviderApi : grant is null ? "2025-01-01" : "2022-04-01",
                    PrincipalId = grant?.PrincipalId, RoleDefinitionId = grant?.RoleDefinitionId, RoleScope = grant?.Scope,
                    ClientId = null, HttpStatus = 201, Incarnation = incarnation, NativeRevision = revision,
                    ProviderRequestId = Guid.NewGuid().ToString(), ProviderCorrelationId = Guid.NewGuid().ToString(),
                };
            }).ToArray();
            Authority = authority with { OperationCount = 6, Effects = effects, EffectHashes = effects.Select(FactoryModelsValidation.Hash).ToArray() };
            Manifest = original with { ModelsOperationId = Authority.OperationId, ModelsApprovalFingerprint = Authority.ApprovalFingerprint };
            Put(ProductConfigurationKeys.FactoryManifest, Manifest);
            var stored = JsonSerializer.SerializeToNode(FactoryModelsValidation.Persisted(Authority), FactoryGitHubJson.Options)!;
            stored["checkId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
            Values[ProductConfigurationKeys.FactoryModels] = stored.ToJsonString();
            for (var i = 0; i < 5; i++) Put(ProductConfigurationKeys.ModelRoute(authority.OperationId, i), routes[i]);
            for (var i = 0; i < 3; i++) Put(ProductConfigurationKeys.ModelBinding(authority.OperationId, FactoryModelsValidation.BindingNames[i]), bindings[i]);
            for (var i = 0; i < 2; i++) Put(ProductConfigurationKeys.ModelGrant(authority.OperationId, i), grants[i]);
            for (var i = 0; i < 6; i++) Put(ProductConfigurationKeys.ModelOperation(authority.OperationId, i), effects[i]);
            Environment = new() { ["DSF_PRODUCT"] = original.Identity.ProductKey, ["AZURE_APPCONFIG_ENDPOINT"] = config.ProductEndpoint,
                ["DSF_FACTORY_PROFILE_PIN"] = config.ProfilePin, ["AZURE_CLIENT_ID"] = model.ClientId };

            ReviewedModelRoute Route(string name, string format, string family, string nativeModel, string version, bool create, int dimensions)
            {
                var selection = new ModelRouteSelection(name, create, Account, create ? prefix + "-" + name : name, format, family,
                    nativeModel, version, "GlobalStandard", 1, dimensions, original.Identity.TenantId, "swedencentral", Endpoint,
                    true, "Existing private model-serving connection.", role);
                var account = JsonSerializer.SerializeToNode(new { id = Account, kind = "AIServices", location = "swedencentral",
                    systemData = new { createdAt = "2026-09-16T11:00:00Z" }, properties = new { endpoint = Endpoint + "/", customSubDomainName = "models",
                        publicNetworkAccess = "Disabled", provisioningState = "Succeeded" } })!;
                Resources[Account] = account;
                var target = FactoryModelsValidation.DeploymentTarget(selection);
                Resources[target] = JsonSerializer.SerializeToNode(new { id = target, etag = "\"native-model-version\"",
                    systemData = new { createdAt = "2026-09-16T11:00:00Z" }, sku = new { name = "GlobalStandard", capacity = 1 },
                    properties = new { provisioningState = "Succeeded", versionUpgradeOption = "NoAutoUpgrade",
                        model = new { format, name = nativeModel, version } } })!;
                return new(selection, FactoryModelsValidation.ObserveAccount(selection, Element(account)),
                    create ? null : FactoryModelsValidation.ObserveDeployment(selection, Element(Resources[target])));
            }
            FoundationOperation Binding(string name) => JsonSerializer.Deserialize<FoundationOperation>(Values.Single(p =>
                p.Key.Contains("/github-bindings/", StringComparison.Ordinal) && p.Key.EndsWith("/" + name, StringComparison.Ordinal)).Value, FactoryGitHubJson.Options)!;
            void Put<T>(string key, T value) => Values[key] = JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
        }

        internal static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.NotNull(request.Headers.Authorization);
            if (request.RequestUri!.Host.EndsWith(".azconfig.io", StringComparison.Ordinal))
                return Reply(new { items = Values.Select(p => new { key = p.Key, value = p.Value, label = (string?)null, etag = "revision" }) });
            if (request.RequestUri.Host == "management.azure.com")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                if (request.RequestUri.AbsolutePath.EndsWith("/deployments/juror-2", StringComparison.Ordinal))
                {
                    if (Scenario == "missing-juror") return new(HttpStatusCode.NotFound);
                    if (Scenario == "mislabeled-juror")
                    {
                        var wrong = Resources[request.RequestUri.AbsolutePath].DeepClone();
                        wrong["properties"]!["model"]!["format"] = "OpenAI";
                        wrong["properties"]!["model"]!["name"] = "gpt-4.1";
                        return Reply(wrong);
                    }
                }
                if (Scenario == "replaced-grant" && Inference.Count >= 3 && request.RequestUri.AbsolutePath.Contains("/roleassignments/", StringComparison.Ordinal))
                {
                    var replaced = Resources[request.RequestUri.AbsolutePath].DeepClone();
                    replaced["properties"]!["createdOn"] = "2026-09-16T11:01:00Z";
                    return Reply(replaced);
                }
                return Resources.TryGetValue(request.RequestUri.AbsolutePath, out var resource) ? Reply(resource) : new(HttpStatusCode.NotFound);
            }
            Assert.Equal("models.services.ai.azure.com", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Inference.Add(body);
            OnInference?.Invoke();
            var route = Authority.Routes!.Single(r => r.Selection.Deployment == (string?)body["model"]).Selection;
            if (route.Name == "juror-2" && Scenario == "network") throw new HttpRequestException("unrelated-secret");
            if (route.Name == "juror-2" && Scenario == "timeout") throw new TaskCanceledException("unrelated-secret");
            if (route.Name == "juror-2" && Scenario == "denied-inference") return new(HttpStatusCode.Forbidden)
                { Content = new StringContent("unrelated-secret") };
            if (route.Name == "embedding")
            {
                Assert.Equal("/openai/v1/embeddings", request.RequestUri.AbsolutePath);
                var count = body["input"]!.AsArray().Count;
                Assert.InRange(count, 2, 32);
                var vectors = Enumerable.Range(0, count).Select(i => i switch
                { 0 => Vector(1, 0, route.Dimensions), 1 => Vector(0.99, 0.01, route.Dimensions), _ => Vector(0, 1, route.Dimensions) }).ToArray();
                if (Scenario == "embedding-empty") vectors = [];
                if (Scenario == "embedding-dimensions") vectors[0] = [1, 0];
                if (Scenario == "embedding-zero") vectors[0] = new double[1536];
                return Reply(new { model = route.Model, data = vectors.Select((v, i) => new { index = Scenario == "embedding-indices" ? 0 : i, embedding = v }) });
            }
            Assert.Equal("/openai/v1/chat/completions", request.RequestUri.AbsolutePath);
            return Reply(new { model = Scenario == "wrong-response-model" && route.Name == "juror-2" ? "gpt-4.1" : route.Model,
                choices = new[] { new { finish_reason = Scenario == "truncated" && route.Name == "synthesis" ? "length" : "stop",
                message = new { content = Scenario == "malformed-judgment" && route.Name == "juror-2" ? "GO:" : route.Name == "synthesis"
                    ? Completion?.Invoke((string)body["messages"]![0]!["content"]!)
                        ?? "Preserve the stable logical problem identity when equivalent descriptions refer to the same underlying problem."
                    : "GO: Preserving stable identifiers avoids filing duplicate issues when descriptions differ but the underlying problem is unchanged." } } } });
        }
        private static double[] Vector(double first, double second, int dimensions)
        {
            var result = Enumerable.Repeat(0.00012345678901234567, dimensions).ToArray();
            result[0] = first;
            result[1] = second;
            return result;
        }
        private static HttpResponseMessage Reply(object body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            response.Headers.Add("x-request-id", Guid.NewGuid().ToString());
            return response;
        }
    }
}
