using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryPublisherTests(PublisherFixture fixture) : IClassFixture<PublisherFixture>
{
    [Theory]
    [InlineData("lost", "Unknown")]
    [InlineData("canceled", "Unknown")]
    [InlineData("denied", "Failed")]
    [InlineData("pause", "Unknown")]
    [InlineData("offboarding", "Unknown")]
    [InlineData("config", "Unknown")]
    public async Task Original_native_publication_rollout_cannot_replay_after_uncertainty_or_governance_race(string failure, string expected)
    {
        var transport = fixture.Reset();
        var selection = Selection();
        var reviewed = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", selection.ToJsonString());
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        if (failure is "lost" or "canceled" or "denied") transport.NativeFailure = failure;
        else transport.Scenario.Arm.Product.BeforePut = async (key, _) =>
        {
            if (key != FactoryPublisher.EffectKey((Guid)selection["operationId"]!, 0)) return;
            transport.Scenario.Arm.Product.BeforePut = null;
            if (failure == "pause") await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            else if (failure == "offboarding")
                transport.Scenario.Arm.Product.Seed(FactoryOffboarding.HistoryKey(Guid.NewGuid()), "{}");
            else transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, original => original);
        };
        var result = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", selection.ToJsonString(),
            "--approve", (string)reviewed.Result["approvalFingerprint"]!);
        Assert.Equal(1, result.Exit);
        Assert.Equal(expected, (string?)result.Result["state"]);
        Assert.False((bool)result.Result["isActivated"]!);
        var records = transport.Scenario.Arm.Product.Export();
        var effects = transport.NativeWrites.ToArray();
        transport.NativeFailure = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var status = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--status");
        Assert.Equal(1, status.Exit);
        Assert.Equal(expected, (string?)status.Result["state"]);
        Assert.Equal(records, transport.Scenario.Arm.Product.Export());
        Assert.Equal(effects, transport.NativeWrites);
        if (failure is "pause" or "offboarding" or "config" or "denied") Assert.Empty(effects);
        else Assert.Single(effects);
        Assert.Empty(transport.PublisherTokenRequests);
    }

    [Fact]
    public async Task Native_publication_rollout_preserves_original_authorities_and_provisions_exact_isolated_capability()
    {
        var transport = fixture.Reset();
        var before = transport.Values();
        var selection = Selection();
        var reviewed = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", selection.ToJsonString());
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output + reviewed.Terminal.Error);
        Assert.Equal("Reviewed", (string?)reviewed.Result["state"]);
        Assert.Equal(before, transport.Values());

        var deployed = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", selection.ToJsonString(),
            "--approve", (string)reviewed.Result["approvalFingerprint"]!);

        Assert.True(deployed.Exit == 0, deployed.Terminal.Output + deployed.Terminal.Error + "\n" + string.Join("\n", transport.HostFailures.TakeLast(3)));
        Assert.Equal("PublicationPreparedAwaitingPromotion", (string?)deployed.Result["state"]);
        Assert.False((bool)deployed.Result["isActivated"]!);
        foreach (var pair in before) Assert.Equal(pair.Value, transport.Values()[pair.Key]);
        Assert.Contains(transport.Resources.Keys, k => k.EndsWith("/containers/proposal-publications", StringComparison.Ordinal));
        Assert.Contains(transport.Resources.Keys, k => k.EndsWith("-publisher", StringComparison.Ordinal));
        Assert.Contains(transport.Resources.Keys, k => k.EndsWith("-pub-context", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.NativeWrites, k => k.Contains("/secrets/", StringComparison.Ordinal));
        Assert.NotEmpty(transport.PublisherTokenRequests);
        Assert.All(transport.PublisherKeyPrincipals, p => Assert.Equal((string?)deployed.Result["plan"]!["gitHubAuthority"]!["authPrincipalId"], p));
        Assert.DoesNotContain(PreviewPreparationTransport.PublisherToken, deployed.Terminal.Output);
        Assert.Contains(transport.Values().Keys, k => k.EndsWith("/native-capability-proof", StringComparison.Ordinal));
        JsonNode? auditRoot = null;
        for (var page = 0; auditRoot is null; page++)
        {
            var audit = await transport.Scenario.Machine.InvokeAsync("audit", "--page", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(audit.Exit == 0, audit.Terminal.Output);
            auditRoot = audit.Result["records"]!.AsArray().SingleOrDefault(r => (string?)r!["identity"]!["key"] == FactoryPublisher.RootKey);
            Assert.True(auditRoot is not null || audit.Result["nextPage"] is not null, "Original publication audit root missing.");
        }
        foreach (var key in new[] { FactoryLive.RootKey, ProductConfigurationKeys.FactoryGitHubAuthority,
            (string)deployed.Result["plan"]!["approval"]!["key"]!, FactoryPublisher.ChunkKey((Guid)selection["operationId"]!, 0),
            FactoryPublisher.ObservationKey((Guid)selection["operationId"]!) })
            Assert.Contains(auditRoot["references"]!.AsArray(), r => (string?)r!["key"] == key);
        var response = await transport.InvokePublisherAsync(deployed.Result, "/publish",
            new FactoryPublicationRequest(Guid.NewGuid(), "arbitrary-user-candidate"));
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("ExplicitCurrentFinalPromotionRequired", response.Body, StringComparison.Ordinal);
        var capabilityRequest = new FactoryLiveRequest(Guid.Parse((string)selection["operationId"]!), (string)deployed.Result["approvalFingerprint"]!);
        foreach (var actor in new[] { "operator", "code" })
        {
            var denied = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest, actor);
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
            Assert.Contains("ExactNativePublicationCallerRequired", denied.Body, StringComparison.Ordinal);
        }
        foreach (var trigger in new[] { "push", "pull_request", "check_run", "check_suite" })
        {
            transport.PublisherWorkflowEvent = trigger;
            var allowed = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest);
            Assert.True(allowed.Status == HttpStatusCode.OK, allowed.Body);
        }
        foreach (var trigger in new[] { "issues", "repository_dispatch", "unrecognized_event" })
        {
            transport.PublisherWorkflowEvent = trigger;
            var denied = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest);
            Assert.Equal(HttpStatusCode.Conflict, denied.Status);
            Assert.Contains("proposal-mutation-review", denied.Body, StringComparison.Ordinal);
        }
        transport.PublisherWorkflowEvent = null;
        var retained = transport.Values();
        transport.PublisherPermissionDrift = true;
        var drift = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest);
        Assert.Equal(HttpStatusCode.Conflict, drift.Status);
        Assert.Contains("github-access", drift.Body, StringComparison.Ordinal);
        Assert.Equal(retained, transport.Values());
        transport.PublisherPermissionDrift = false;
        var forged = await transport.InvokePublisherAsync(deployed.Result, "/publish",
            new { invocationId = Guid.NewGuid(), candidateId = "candidate", liveFiling = true, title = "unreviewed" });
        Assert.Equal(HttpStatusCode.Conflict, forged.Status);
        var nativeRole = transport.Resources.Single(p => p.Key.Contains("/sqlroledefinitions/", StringComparison.Ordinal)
            && (string?)p.Value["properties"]?["roleName"] == "dsf-publication-writer");
        var original = nativeRole.Value.DeepClone();
        nativeRole.Value["properties"]!["permissions"]![0]!["dataActions"]!.AsArray().Add(
            "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/delete");
        var broadened = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest);
        Assert.NotEqual(HttpStatusCode.OK, broadened.Status);
        transport.Resources[nativeRole.Key] = original;
        var originalProof = transport.Scenario.Arm.Product.Export();
        transport.Scenario.Arm.Product.Change(FactoryPublisher.ObservationKey((Guid)selection["operationId"]!), value => value);
        var changedProof = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest);
        Assert.Equal(HttpStatusCode.Forbidden, changedProof.Status);
        Assert.Contains("OriginalAuthenticatedPublisherProofChanged", changedProof.Body, StringComparison.Ordinal);
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(originalProof);
        var paused = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.Contains("DrainingUnknown", paused.Terminal.Output, StringComparison.Ordinal);
        var afterPause = await transport.InvokePublisherAsync(deployed.Result, "/capability", capabilityRequest);
        Assert.Equal(HttpStatusCode.Forbidden, afterPause.Status);
        Assert.DoesNotContain(transport.SeedDocuments.Keys, k => k.Contains("/colls/proposal-publications/", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Requests, r => r.Method == "POST" && r.Uri.Host == "api.github.com" && r.Uri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal));
    }

    internal static JsonNode Selection() => JsonSerializer.SerializeToNode(new
    {
        operationId = Guid.Parse("bc000000-0000-0000-0000-000000000216"),
        runtimeImage = FactoryTelemetryWorkflowTests.Image,
        audienceClientId = "bc111111-1111-1111-1111-111111111111",
        metadataRoleDefinitionId = PreviewPreparationTransport.CouncilRole,
    })!;

    internal static void ConfigureInitialApp(BoundaryPlanFixture fixture)
    {
        fixture.Repository.SetWorkflow("on: push\njobs:\n  ci:\n    steps:\n      - run: dotnet test\n");
        foreach (var path in new[] { "/app", "/app/installations/42" })
        {
            var response = fixture.Repository.AppResponses[path];
            var body = JsonNode.Parse(response.Json)!;
            body["permissions"]!["actions"] = "read";
            body["permissions"]!["repository_hooks"] = "read";
            fixture.Repository.AppResponses[path] = (response.Status, body.ToJsonString());
        }
        fixture.Repository.Responses["repos/acme/renamed/labels/dsf%3Aproposal"] = new(0,
            JsonSerializer.Serialize(new { id = 10, name = "dsf:proposal", color = "123456", description = DecideRepositoryLabels.Meanings["dsf:proposal"] }), "");
    }
}

public sealed class PublisherFixture : IAsyncLifetime
{
    private PreviewPreparationTransport transport = null!;
    private string product = "";
    private Dictionary<string, JsonNode> resources = [];
    private Dictionary<string, JsonNode[]> operations = [];
    private Dictionary<string, string> nativeDocuments = [];
    public Task InitializeAsync() => InitializeAsync(null);
    internal async Task InitializeAsync(TimeSpan? applicationRecordAge, bool selectedSources = false)
    {
        transport = await FactoryActivationTests.ReadyAsync(publisher: true, applicationRecordAge: applicationRecordAge, selectedSources: selectedSources);
        var github = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryGitHubAuthority])!;
        Assert.Equal("read", (string?)github["expectedAppPermissions"]!["repository_hooks"]);
        var operation = Guid.NewGuid().ToString();
        var reviewed = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation);
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        Assert.Equal("ApprovalOnly", (string?)(await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation,
            "--approve", (string)reviewed.Result["approvalFingerprint"]!)).Result["state"]);
        var selection = FactoryPublisherTests.Selection().DeepClone();
        selection["operationId"] = Guid.NewGuid();
        var live = await transport.Scenario.Machine.InvokeAsync("live", "--selection", selection.ToJsonString());
        Assert.True(live.Exit == 0, live.Terminal.Output);
        var native = await transport.Scenario.Machine.InvokeAsync("live", "--selection", selection.ToJsonString(),
            "--approve", (string)live.Result["approvalFingerprint"]!);
        Assert.True(native.Exit == 0, native.Terminal.Output);
        CaptureCurrent();
    }
    internal void CaptureCurrent()
    {
        product = transport.Scenario.Arm.Product.Export();
        resources = transport.Resources.ToDictionary(p => p.Key, p => p.Value.DeepClone());
        operations = transport.DeploymentOperations.ToDictionary(p => p.Key, p => p.Value.Select(v => v.DeepClone()).ToArray());
        nativeDocuments = transport.SeedDocuments.ToDictionary();
    }
    internal PreviewPreparationTransport Reset(DateTimeOffset? now = null)
    {
        transport.Restart(now);
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(product);
        transport.SeedDocuments.Clear();
        foreach (var pair in nativeDocuments) transport.SeedDocuments.Add(pair.Key, pair.Value);
        transport.Resources.Clear();
        foreach (var pair in resources) transport.Resources.Add(pair.Key, pair.Value.DeepClone());
        transport.DeploymentOperations.Clear();
        foreach (var pair in operations) transport.DeploymentOperations.Add(pair.Key, pair.Value.Select(v => v.DeepClone()).ToArray());
        transport.NativeWrites.Clear();
        transport.HostFailures.Clear();
        transport.NativeFailure = null;
        transport.AfterNativeWrite = null;
        transport.BeforeIngressRequest = null;
        transport.SeedFailure = null;
        transport.PublisherTokenRequests.Clear();
        transport.PublisherKeyPrincipals.Clear();
        transport.PublisherPermissionDrift = false;
        transport.PublisherWorkflowEvent = null;
        transport.ResetPublications();
        FactoryActivationTests.ConnectAudit(transport);
        return transport;
    }
    public Task DisposeAsync() { transport?.Dispose(); return Task.CompletedTask; }
}
