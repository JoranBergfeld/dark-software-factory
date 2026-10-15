using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.Runtime;
using Dsf.Runtime.Tests;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactorySourceWorkflowTests
{
    internal static readonly string[] Preparation = ["--prepare", "--runtime-image", FactoryGitHubAuthorityWorkflowTests.Preparation[5],
        "--source-audience", "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"];

    [Fact]
    public async Task Default_CLI_status_rejects_missing_nonselected_chunk_after_serialized_worker_replacement()
    {
        static void Populate(SourceInventoryTransport source) => source.RootTree = source.Tree(Enumerable.Range(0, 40)
            .Select(i => ($"file-{i:D2}.py", "100644", source.Blob("print('first party')"))).ToArray());
        var (machine, arm, previous) = await ReadyAsync();
        using (previous)
        using (var transport = new SourceWorkflowTransport(arm, previous))
        {
            machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var preview = await machine.InvokeAsync("source", Preparation);
            await machine.InvokeAsync("source", [.. Preparation, "--approve", (string)preview.Result["source"]!["approvalFingerprint"]!]);
            Assert.NotNull(transport.Source);
            Populate(transport.Source);
            var acquired = await machine.InvokeAsync("source");
            for (var i = 0; i < 16 && acquired.Exit != 0; i++) acquired = await machine.InvokeAsync("source");
            Assert.Equal(0, acquired.Exit);
            using var replacement = new SourceInventoryTransport(transport.Values(), transport.Source.Clock.Now);
            Populate(replacement);
            replacement.ImportProgress(transport.Source);
            replacement.Progress.Remove(replacement.Progress.Keys.Single(k => k.Contains("/chunks/00000033/", StringComparison.Ordinal)));
            transport.Source.Dispose();
            transport.Source = replacement;
            var status = await machine.InvokeAsync("source", "--status-only");
            Assert.Equal(1, status.Exit);
            Assert.Equal("Unknown", (string?)status.Result["state"]);
            Assert.Contains("MissingChunk", (string?)status.Result["detail"]);
            Assert.Empty(replacement.Writes);
        }
    }

    [Fact]
    public async Task Source_preparation_cannot_attach_to_a_replaced_foundation_network()
    {
        var (machine, arm, previous) = await ReadyAsync();
        using (previous)
        using (var transport = new SourceWorkflowTransport(arm, previous) { ReplacedNetwork = true })
        {
            machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var preview = await machine.InvokeAsync("source", Preparation);
            Assert.Equal("Blocked", (string?)preview.Result["state"]);
            Assert.Empty(transport.Deployments);
        }
    }

    [Theory]
    [InlineData("deployment")]
    [InlineData("acquisition")]
    public async Task Lost_remote_responses_are_unknown_and_never_recreate_source_infrastructure(string stage)
    {
        var (machine, arm, previous) = await ReadyAsync();
        using (previous)
        using (var transport = new SourceWorkflowTransport(arm, previous))
        {
            machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var preview = await machine.InvokeAsync("source", Preparation);
            transport.LoseDeployment = stage == "deployment";
            var prepared = await machine.InvokeAsync("source", [.. Preparation, "--approve", (string)preview.Result["source"]!["approvalFingerprint"]!]);
            if (stage == "deployment") Assert.Equal("Unknown", (string?)prepared.Result["state"]);
            else
            {
                transport.LoseAcquisition = true;
                var acquired = await machine.InvokeAsync("source");
                Assert.Equal("Unknown", (string?)acquired.Result["state"]);
            }
            var repeat = await machine.InvokeAsync("source", Preparation);
            Assert.NotEqual(0, repeat.Exit);
            Assert.Single(transport.Deployments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interactive_approval_rechecks_governance_and_preserves_explicit_command(bool changed)
    {
        var (machine, arm, previous) = await ReadyAsync();
        using (previous)
        using (var transport = new SourceWorkflowTransport(arm, previous))
        {
            machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var preview = await machine.InvokeAsync("source", Preparation);
            var terminal = new ScriptedTerminal(new(true, false, false), [(string)preview.Result["source"]!["approvalFingerprint"]!])
            { OnPrompt = _ => { if (changed) arm.Product.Remove(ProductConfigurationKeys.FactoryProfile); } };
            await machine.Boundary.Repository.InvokeCommandAsync(terminal,
                ["onboard", "decide", "source", "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
                    "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription, .. Preparation]);
            Assert.Single(terminal.Prompts);
            Assert.Contains("dsf onboard decide source --prepare", terminal.Output);
            Assert.Equal(changed ? 0 : 1, transport.Deployments.Count);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Operator_prepares_source_only_store_and_code_host_then_real_host_acquires_under_its_own_identity(bool networkEtags, bool v2)
    {
        var (machine, arm, previous) = await ReadyAsync();
        using (previous)
        using (var transport = new SourceWorkflowTransport(arm, previous) { NetworkEtags = networkEtags })
        {
            machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var owner = machine.Store.Export();
            var preview = await machine.InvokeAsync("source", Preparation);
            Assert.True((string?)preview.Result["source"]?["stage"] == "preview", preview.Terminal.Output + preview.Terminal.Error + preview.Result.ToJsonString());
            Assert.Empty(transport.Deployments);
            var prepared = await machine.InvokeAsync("source", [.. Preparation, "--approve", (string)preview.Result["source"]!["approvalFingerprint"]!]);
            Assert.True((string?)prepared.Result["source"]?["stage"] == "prepared", prepared.Terminal.Output);
            Assert.NotNull(transport.Source);
            transport.Source.V2Tokens = v2;
            var acquired = await machine.InvokeAsync("source");
            Assert.True(acquired.Exit == 0, acquired.Terminal.Output + string.Join("\n", transport.Source!.Requests.TakeLast(5)) + "\n" + transport.HostDiagnostic);
            Assert.Equal("Acquired", (string?)acquired.Result["state"]);
            Assert.Equal("NotYetAnalyzed", (string?)acquired.Result["analysis"]);
            Assert.Equal(913, (long)acquired.Result["progress"]!["snapshot"]!["repositoryId"]!);
            Assert.Single(transport.Deployments);
            Assert.Equal(owner, machine.Store.Export());
            Assert.Equal("Inactive", (string?)transport.Record(ProductConfigurationKeys.FactoryManifest)["publication"]);
            Assert.DoesNotContain("First party application documentation", arm.Product.Export());
            Assert.NotEmpty(transport.Source!.Progress);
            Assert.All(transport.Source.Writes, key => Assert.StartsWith("source/", key));
            Assert.NotEmpty(transport.Source.BrokerClaims);
            Assert.All(transport.Source.BrokerClaims, claims =>
            {
                Assert.Equal(v2 ? "2.0" : "1.0", (string?)claims["ver"]);
                Assert.Equal((v2 ? "" : "api://") + transport.Source.GitHub.AudienceClientId, (string?)claims["aud"]);
            });
        }
    }

    internal static async Task<(ReservationMachine Machine, FactoryFoundationTests.FoundationTransport Arm,
        FactoryGitHubAuthorityWorkflowTests.AuthorityWorkflowTransport Previous)> ReadyAsync(bool telemetry = false, bool publisher = false)
    {
        var (machine, arm) = telemetry
            ? await FactoryTelemetryWorkflowTests.FoundationAsync(creationClock: new ReservationClock(),
                configure: publisher ? FactoryPublisherTests.ConfigureInitialApp : null)
            : await FactoryGitHubAuthorityWorkflowTests.FoundationAsync();
        var preview = await machine.InvokeAsync("github", FactoryGitHubAuthorityWorkflowTests.Preparation);
        var transport = new FactoryGitHubAuthorityWorkflowTests.AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. FactoryGitHubAuthorityWorkflowTests.Preparation,
            "--approve", (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(0, result.Exit);
        return (machine, arm, transport);
    }
}

internal sealed class SourceWorkflowTransport(FactoryFoundationTests.FoundationTransport foundation, HttpMessageHandler previous) : HttpMessageHandler
{
    private readonly HttpMessageInvoker fallback = new(previous, false);
    internal SourceInventoryTransport? Source;
    internal readonly List<JsonNode> Deployments = [];
    internal readonly Dictionary<string, JsonNode> Resources = new(StringComparer.OrdinalIgnoreCase);
    private JsonNode? deployment;
    internal string HostDiagnostic = "";
    internal bool ReplacedNetwork { get; set; }
    internal bool LoseDeployment { get; set; }
    internal bool LoseAcquisition { get; set; }
    internal bool NetworkEtags { get; set; }
    internal Func<SourceInventoryTransport, HttpMessageHandler>? WorkloadTransport { get; set; }
    internal Action<string, string>? AfterHost { get; set; }
    internal Dictionary<string, string> Values() => JsonNode.Parse(foundation.Product.ListJson())!.AsArray()
        .ToDictionary(n => (string)n!["key"]!, n => (string)n!["value"]!);
    internal JsonNode Record(string key) => JsonNode.Parse(Values()[key])!;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        if (uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal)) return await fallback.SendAsync(request, token);
        if (uri.Host == "source.private.azurecontainerapps.io")
        {
            Assert.NotNull(Source);
            Source.Values = Values();
            using var workload = WorkloadTransport?.Invoke(Source);
            using var http = new HttpClient(workload ?? Source, false);
            var runner = new SourceHostRunner(Source)
            {
                Route = uri.AbsolutePath,
                Request = await request.Content!.ReadFromJsonAsync<JsonNode>(FactoryGitHubJson.Options, token) ?? throw new JsonException(),
            };
            var dependencies = RuntimeDependencies.Production() with
                { WebHostRunner = runner, SourceHttp = http, SourceCredential = new SourceTestCredential(Source), SourceClock = Source.Clock };
            var exit = await RuntimeCliApplication.InvokeAsync(["serve-source"], Source.Environment,
                new StringWriter(), new StringWriter(), dependencies, token);
            HostDiagnostic = runner.Status + " " + runner.Body;
            Assert.Equal(0, exit);
            AfterHost?.Invoke(uri.AbsolutePath, runner.Body);
            if (LoseAcquisition) throw new HttpRequestException("Lost source response");
            return new(runner.Status) { Content = new StringContent(runner.Body, System.Text.Encoding.UTF8, "application/json") };
        }
        if (uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
            return await fallback.SendAsync(request, token);
        Assert.Equal("management.azure.com", uri.Host);
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (request.Method == HttpMethod.Delete)
            return await fallback.SendAsync(request, token);
        if (request.Method == HttpMethod.Get)
        {
            if (ReplacedNetwork && path.EndsWith("-network", StringComparison.Ordinal))
                return SourceInventoryTransport.Json(new { id = path, systemData = new { createdAt = "2000-01-01T00:00:00Z" }, properties = new { } });
            if (Resources.TryGetValue(path, out var resource)) return ModelsWorkflowTransport.Reply(HttpStatusCode.OK, resource);
            using var native = new SourceInventoryTransport(Values(), new ReservationClock().GetUtcNow());
            if (path.EndsWith("/roleassignments", StringComparison.Ordinal))
                return SourceInventoryTransport.Json(new { value = Resources.Where(p => p.Key.Contains("/roleassignments/", StringComparison.Ordinal)).Select(p => p.Value).ToArray() });
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
                return SourceInventoryTransport.Json(new { value = new[] { new { actions = new[] { "*" }, notActions = Array.Empty<string>() } } });
            if (path.EndsWith("/denyassignments", StringComparison.Ordinal)) return SourceInventoryTransport.Json(new { value = Array.Empty<object>() });
            if (native.Resources.TryGetValue(path, out var existing) && (path.Contains("/roledefinitions/", StringComparison.Ordinal)
                || path.Contains("/userassignedidentities/", StringComparison.Ordinal) || path.Contains("/managedenvironments/", StringComparison.Ordinal)))
                return SourceInventoryTransport.Json(existing);
            if (deployment is not null && path == (string?)deployment["id"]) return SourceInventoryTransport.Json(deployment);
            if (deployment is not null && path == (string?)deployment["id"] + "/operations")
                return SourceInventoryTransport.Json(new { value = Resources.Select((r, i) => new
                {
                    operationId = i.ToString(), properties = new { targetResource = new { id = r.Key }, provisioningOperation = "Create",
                        provisioningState = "Succeeded", statusCode = "Created", trackingId = Guid.NewGuid().ToString() },
                }).ToArray() });
            return await fallback.SendAsync(request, token);
        }
        if (path.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal))
            return await fallback.SendAsync(request, token);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("/providers/microsoft.resources/deployments/sc-", path);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        Deployments.Add(body);
        Source = new SourceInventoryTransport(Values(), new ReservationClock().GetUtcNow());
        foreach (var target in FactorySourceValidation.Targets(Source.Manifest, Source.GitHub))
        {
            Resources[target.Target] = Source.Resources[target.Target].DeepClone();
            if (NetworkEtags && target.Target.Contains("/microsoft.network/", StringComparison.Ordinal))
            {
                Resources[target.Target].AsObject().Remove("systemData");
                Resources[target.Target]["etag"] = "\"native-network-etag\"";
                Source.Resources[target.Target] = Resources[target.Target].DeepClone();
            }
        }
        deployment = JsonSerializer.SerializeToNode(new { id = path, properties = new { provisioningState = "Succeeded", correlationId = Guid.NewGuid().ToString() } })!;
        if (LoseDeployment) throw new HttpRequestException("Lost deployment response");
        return ModelsWorkflowTransport.Reply(HttpStatusCode.Created, deployment);
    }
}
