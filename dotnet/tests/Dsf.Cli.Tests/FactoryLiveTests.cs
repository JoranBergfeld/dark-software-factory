using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Dsf.Core.Runtime;
using Dsf.Core.Onboarding;
using Dsf.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryLiveTests(ActivationAuthorityFixture fixture) : IClassFixture<ActivationAuthorityFixture>
{
    [Fact]
    public async Task Operator_reviews_and_provisions_exact_native_live_host_without_promoting_old_approval()
    {
        var transport = await ResetAsync();
        var original = transport.Values();
        var selection = Selection();
        var reviewed = await transport.Scenario.Machine.InvokeAsync("live", "--selection", selection.ToJsonString());
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output + reviewed.Terminal.Error);
        Assert.Equal("Reviewed", (string?)reviewed.Result["state"]);
        Assert.Equal(original, transport.Values());
        var deployed = await transport.Scenario.Machine.InvokeAsync("live", "--selection", selection.ToJsonString(),
            "--approve", (string)reviewed.Result["approvalFingerprint"]!);
        Assert.True(deployed.Exit == 0, deployed.Terminal.Output + deployed.Terminal.Error);
        Assert.Equal("PreparedAwaitingPublication", (string?)deployed.Result["state"]);
        Assert.False((bool)deployed.Result["isActivated"]!);
        Assert.False((bool)deployed.Result["historicallyCompleted"]!);
        Assert.Equal(2, deployed.Result["receipts"]!.AsArray().Count(r => !((string)r!["target"]!).Contains("/roleassignments/", StringComparison.Ordinal)));
        foreach (var pair in original) Assert.Equal(pair.Value, transport.Values()[pair.Key]);
        Assert.DoesNotContain(transport.NativeWrites, target => target.Contains("/sql", StringComparison.Ordinal));
        var nativeBeforePublication = transport.NativeWrites.ToArray();
        var unsupported = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", Selection().ToJsonString());
        Assert.Equal(1, unsupported.Exit);
        Assert.Contains("GitHubAppAdministratorPrerequisiteBeforeInitialAuthorityApproval", unsupported.Terminal.Output, StringComparison.Ordinal);
        Assert.Equal(nativeBeforePublication, transport.NativeWrites);
        var live = FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
            transport.Scenario.Source.Source!.Clock.GetUtcNow());
        var invocation = new FactoryLiveRequest(Guid.NewGuid(), live.Plan.Fingerprint);
        var originalDocuments = transport.SeedDocuments.ToDictionary();
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var accepted = await http.PostAsJsonAsync(live.Endpoint + "/exercise", invocation, FactoryGitHubJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, accepted.StatusCode);
        Assert.Contains("ReviewedNativePublicationCapabilityRequired", await accepted.Content.ReadAsStringAsync());
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
        Assert.Equal(originalDocuments, transport.SeedDocuments);
        var execution = await transport.Scenario.Machine.InvokeAsync("live", "--execution", invocation.OperationId.ToString(),
            "--approve", live.Plan.Fingerprint);
        Assert.Equal(1, execution.Exit);
        Assert.Equal("NativeNonpublishingExercise", (string?)execution.Result["mode"]);
        Assert.False((bool)execution.Result["publicationPermitted"]!);
        var completedDocuments = transport.SeedDocuments.ToDictionary();
        using var duplicate = await http.PostAsJsonAsync(live.Endpoint + "/exercise", invocation, FactoryGitHubJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
        Assert.Equal(completedDocuments, transport.SeedDocuments);
        using var incompatible = await http.PostAsJsonAsync(live.Endpoint + "/exercise",
            invocation with { RolloutFingerprint = new string('a', 64) }, FactoryGitHubJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, incompatible.StatusCode);
        Assert.Equal(completedDocuments, transport.SeedDocuments);
        var graphRecords = new List<JsonNode?>();
        for (var page = 0; ; page++)
        {
            var audit = await transport.Scenario.Machine.InvokeAsync("audit", "--page", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(audit.Exit == 0, audit.Terminal.Output);
            graphRecords.AddRange(audit.Result["records"]!.AsArray());
            if (audit.Result["nextPage"] is null) break;
        }
        var root = graphRecords.Single(r => (string?)r!["identity"]!["key"] == FactoryLive.RootKey)!;
        Assert.Contains(root["references"]!.AsArray(), r => (string?)r!["key"] == live.Plan.Approval.Key);
        Assert.Contains(root["references"]!.AsArray(), r => (string?)r!["key"] == FactoryLive.ChunkKey(live.Coordinator.OperationId, 0));
        var saved = transport.Scenario.Arm.Product.Export();
        var writes = transport.NativeWrites.Count;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var status = await transport.Scenario.Machine.InvokeAsync("live", "--status");
        Assert.True(status.Exit == 0, status.Terminal.Output + status.Terminal.Error);
        Assert.Equal("PreparedAwaitingPublication", (string?)status.Result["state"]);
        Assert.Equal(saved, transport.Scenario.Arm.Product.Export());
        Assert.Equal(writes, transport.NativeWrites.Count);
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
        var approval = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.True((bool)approval.Result["approvalCurrent"]!);
        Assert.False((bool)approval.Result["isActivated"]!);
        Assert.Equal("Inactive", (string?)JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryManifest])!["publication"]);
        using var registration = new HttpClient(transport.Scenario.Arm.Product.Client());
        var dependencies = RuntimeDependencies.Production(new Dictionary<string, string?>()) with
        {
            ConfigurationSettingsGateway = new AzureConfigurationSettingsGateway(new FactoryActivationAuthorityTests.ReadOnlyCredential(), registration),
            ConveyorComposer = new FactoryActivationAuthorityTests.ForbiddenComposer(),
            SweepLeaseFactory = _ => throw new InvalidOperationException("Forbidden legacy lease."),
        };
        var runtime = new RuntimeSettings("widget", $"https://dsfo-{transport.Manifest.Identity.FactoryId:N}.azconfig.io", "", "",
            "https://cosmos.invalid", "https://model.invalid", "gpt", "embedding", "", "", "", "");
        var env = new Dictionary<string, string?>
        {
            [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
            [RuntimeIntegrationSettings.FactoryProfilePin] = transport.Manifest.ProfilePin,
        };
        var signal = SignalReader.ReadFromJson("""{"product_hints":"widget","source_kinds":["azuremonitor"]}""", "reviewed-live-rollout", false);
        Assert.Contains("Verified product governance", (await Assert.ThrowsAsync<RuntimeVerbException>(() =>
            RuntimeVerbs.RunSignalAsync(runtime, signal, dependencies, CancellationToken.None, env))).Message);
        Assert.Contains("Verified product governance", (await Assert.ThrowsAsync<RuntimeVerbException>(() =>
            RuntimeVerbs.SweepAsync(runtime, false, dependencies, CancellationToken.None, env))).Message);
        var deployment = live.Plan.Deployment.Steps[0].Target;
        var nativeOperation = transport.DeploymentOperations[deployment][0]["properties"]!;
        nativeOperation["provisioningOperation"] = "Update";
        using var unverifiableCreation = await http.PostAsJsonAsync(live.Endpoint + "/execution", invocation, FactoryGitHubJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, unverifiableCreation.StatusCode);
        nativeOperation["provisioningOperation"] = "Create";
        var hostTarget = FactoryLive.HostTarget(live.Plan);
        var retainedHost = transport.Resources[hostTarget].DeepClone();
        transport.Resources[hostTarget]["properties"]!["template"]!["containers"]![0]!["image"] = "unreviewed:mutable";
        using var drift = await http.PostAsJsonAsync(live.Endpoint + "/execution", invocation, FactoryGitHubJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, drift.StatusCode);
        transport.Resources[hostTarget] = retainedHost;
        var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        using var stopped = await http.PostAsJsonAsync(live.Endpoint + "/exercise",
            invocation with { OperationId = Guid.NewGuid() }, FactoryGitHubJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, stopped.StatusCode);
        foreach (var pair in completedDocuments) Assert.Equal(pair.Value, transport.SeedDocuments[pair.Key]);
        Assert.Equal(completedDocuments.Count, transport.SeedDocuments.Count);
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
    }

    [Theory]
    [InlineData("lost", "Unknown")]
    [InlineData("canceled", "Unknown")]
    [InlineData("denied", "Failed")]
    [InlineData("pause", "Unknown")]
    [InlineData("config", "Unknown")]
    public async Task Native_rollout_retains_original_uncertainty_and_never_replays(string failure, string state)
    {
        var transport = await ResetAsync();
        var selection = Selection();
        var reviewed = await transport.Scenario.Machine.InvokeAsync("live", "--selection", selection.ToJsonString());
        Assert.Equal(0, reviewed.Exit);
        if (failure is "lost" or "canceled" or "denied") transport.NativeFailure = failure;
        else transport.Scenario.Arm.Product.BeforePut = async (key, _) =>
        {
            if (key != FactoryLive.EffectKey((Guid)selection["operationId"]!, 0)) return;
            transport.Scenario.Arm.Product.BeforePut = null;
            if (failure == "pause") await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            else transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value => value);
        };
        var executed = await transport.Scenario.Machine.InvokeAsync("live", "--selection", selection.ToJsonString(),
            "--approve", (string)reviewed.Result["approvalFingerprint"]!);
        Assert.Equal(1, executed.Exit);
        Assert.Equal(state, (string?)executed.Result["state"]);
        Assert.False((bool)executed.Result["isActivated"]!);
        Assert.True(transport.Values().ContainsKey(FactoryLive.RootKey));
        var nativeWrites = transport.NativeWrites.Count;
        var saved = transport.Scenario.Arm.Product.Export();
        transport.NativeFailure = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var status = await transport.Scenario.Machine.InvokeAsync("live", "--status");
        Assert.Equal(1, status.Exit);
        Assert.Equal(failure == "denied" ? "Failed" : "Unknown", (string?)status.Result["state"]);
        Assert.Equal(saved, transport.Scenario.Arm.Product.Export());
        Assert.Equal(nativeWrites, transport.NativeWrites.Count);
        Assert.DoesNotContain(transport.NativeWrites, target => target.Contains("/roleassignments/", StringComparison.Ordinal));
        if (failure is "pause" or "config") Assert.Empty(transport.NativeWrites);
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("conflict")]
    public async Task Live_rollout_requires_explicit_complete_selection_before_access(string mode)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        var args = mode == "invalid" ? new[] { "--selection", "{}" }
            : mode == "conflict" ? ["--status", "--selection", Selection().ToJsonString()] : Array.Empty<string>();
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "live", "--json",
            "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
            "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription, .. args],
            CancellationToken.None, terminal);
        Assert.Equal(1, exit);
        Assert.Contains("\"state\": \"Blocked\"", terminal.Output);
        Assert.Empty(terminal.Prompts);
        Assert.Empty(terminal.Error);
    }

    internal static JsonObject Selection() => new()
    {
        ["operationId"] = Guid.NewGuid(),
        ["runtimeImage"] = FactoryTelemetryWorkflowTests.Image,
        ["audienceClientId"] = "bc111111-1111-1111-1111-111111111111",
        ["metadataRoleDefinitionId"] = PreviewPreparationTransport.CouncilRole,
    };

    private async Task<PreviewPreparationTransport> ResetAsync()
    {
        var transport = await fixture.ResetApprovedAsync();
        var host = "/containerapps/" + FactoryPreviewValidation.Prefix(transport.Manifest.Identity.FactoryId) + "-decide";
        foreach (var key in transport.Resources.Keys.Where(k => k.Contains("/deployments/live-", StringComparison.Ordinal)
            || k.Contains(host, StringComparison.Ordinal)).ToArray()) transport.Resources.Remove(key);
        foreach (var key in transport.DeploymentOperations.Keys.Where(k => k.Contains("/deployments/live-", StringComparison.Ordinal)).ToArray())
            transport.DeploymentOperations.Remove(key);
        transport.NativeWrites.Clear();
        transport.NativeFailure = null;
        return transport;
    }
}
