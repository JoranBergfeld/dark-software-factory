using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.Runtime;
using Dsf.Runtime.Tests;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryIntentCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_operator_invocation_reaches_real_hosts_and_rechecks_final_owner_authority(bool loseOwner)
    {
        var (machine, arm, previous) = await FactorySourceWorkflowTests.ReadyAsync();
        using (previous)
        using (var source = new SourceWorkflowTransport(arm, previous))
        using (var models = new ModelsWorkflowTransport(arm))
        {
            machine.Boundary.Repository.ReservationHttp = new HttpClient(source);
            var preview = await machine.InvokeAsync("source", FactorySourceWorkflowTests.Preparation);
            await machine.InvokeAsync("source", [.. FactorySourceWorkflowTests.Preparation,
                "--approve", (string)preview.Result["source"]!["approvalFingerprint"]!]);
            machine.Boundary.Repository.ReservationHttp = new HttpClient(models);
            var modelPreview = await machine.InvokeAsync("models", "--model-topology", models.Topology.ToJsonString());
            var prepared = await machine.InvokeAsync("models", "--model-topology", models.Topology.ToJsonString(),
                "--approve", (string)modelPreview.Result["models"]!["approvalFingerprint"]!);
            Assert.Equal("prepared", (string?)prepared.Result["models"]?["stage"]);
            Assert.Equal(0, (await machine.InvokeAsync("models", "--verify-only")).Exit);
            machine.Boundary.Repository.ReservationHttp = new HttpClient(source);
            source.WorkloadTransport = transport => new IntentCliWorkloadTransport(transport, models);
            source.Source!.RootTree = source.Source.Tree(("README.md", "100644", source.Source.Blob(IntentModelFixture.SourceText)));
            Assert.Equal(0, (await machine.InvokeAsync("source")).Exit);
            var owner = machine.Store.Export();
            var governance = arm.Product.Export();
            var first = await machine.InvokeAsync("infer");
            Assert.True(first.Exit == 1 && (string?)first.Result["state"] == "Partial", first.Terminal.Output);
            if (loseOwner) source.AfterHost = (path, body) =>
            {
                if (path == "/infer" && (string?)JsonNode.Parse(body)!["state"] == "DraftReady")
                    machine.Store.Remove("dsf/onboarding/products/widget");
            };
            var ready = await machine.InvokeAsync("infer");
            if (loseOwner)
            {
                Assert.Equal(1, ready.Exit);
                Assert.Equal("Unknown", (string?)ready.Result["state"]);
                Assert.Null(ready.Result["draft"]);
                Assert.Equal(governance, arm.Product.Export());
                return;
            }
            Assert.True(ready.Exit == 0, ready.Terminal.Output);
            Assert.Equal("DraftReady", (string?)ready.Result["state"]);
            Assert.Equal("Unknown", (string?)ready.Result["draft"]?["successMeasures"]?["knowledge"]?["state"]);
            Assert.False((bool)ready.Result["governs"]!);
            var writes = source.Source.Writes.Count;
            Assert.Equal(0, (await machine.InvokeAsync("infer", "--status-only")).Exit);
            Assert.Equal(writes, source.Source.Writes.Count);
            Assert.Equal(owner, machine.Store.Export());
            Assert.Equal(governance, arm.Product.Export());
            Assert.Single(source.Deployments);
            var interactive = new ScriptedTerminal(new(true, false, false),
                ["https://owner.azconfig.io", "widget", BoundaryPlanFixture.Tenant, BoundaryPlanFixture.Subscription]);
            var inspected = await machine.Boundary.Repository.InvokeCommandAsync(interactive,
                ["onboard", "decide", "infer", "--status-only"]);
            Assert.Equal(0, inspected);
            Assert.Equal(4, interactive.Prompts.Count);
            Assert.Contains("dsf onboard decide infer --owner-appconfig-endpoint", interactive.Output);
            Assert.Contains("Coordinate volunteer shifts", interactive.Output);
            Assert.Equal(writes, source.Source.Writes.Count);
            Assert.Equal(5, ready.Result["modelAuthority"]!["routes"]!.AsArray().Count);
            var manifest = JsonSerializer.Deserialize<FactoryManifest>(source.Source.Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
            var authority = FactoryModelsValidation.ReadPreparation(manifest, source.Source.Values);
            var grant = authority.Grants!.Single(g => g.Scope == authority.Routes![0].Selection.AccountResourceId);
            Assert.True(models.Resources.Remove(grant.Target));
            var revoked = await machine.InvokeAsync("infer", "--status-only");
            Assert.Equal(1, revoked.Exit);
            Assert.Equal("Blocked", (string?)revoked.Result["state"]);
            Assert.Null(revoked.Result["draft"]);
            Assert.Equal(writes, source.Source.Writes.Count);
            Assert.Equal(owner, machine.Store.Export());
            Assert.Equal(governance, arm.Product.Export());
        }
    }

    [Fact]
    public async Task Redirected_inference_requires_complete_explicit_locators_without_prompting()
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "infer", "--json"], CancellationToken.None, terminal);
        Assert.Equal(1, exit);
        Assert.Contains("Blocked", terminal.Output);
        Assert.Contains("required", terminal.Output);
        Assert.Empty(terminal.Prompts);
    }

    internal sealed class IntentCliWorkloadTransport(SourceInventoryTransport source, ModelsWorkflowTransport models,
        Func<string, string>? complete = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker sourceHttp = new(source, false);
        private readonly HttpMessageInvoker modelHttp = new(models, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "models.private.azurecontainerapps.io")
            {
                var manifest = JsonSerializer.Deserialize<FactoryManifest>(source.Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
                var authority = FactoryModelsValidation.ReadPreparation(manifest, source.Values);
                using var native = new IntentNativeModelTransport(source, models, complete);
                using var http = new HttpClient(native);
                var runner = new IntentModelRunner(source, uri.AbsolutePath,
                    await request.Content!.ReadFromJsonAsync<JsonNode>(cancellationToken: token) ?? throw new JsonException());
                var environment = new Dictionary<string, string?>(source.Environment) { ["AZURE_CLIENT_ID"] = authority.Bindings![0].ClientId };
                await RuntimeCliApplication.InvokeAsync(["serve-models"], environment, new StringWriter(), new StringWriter(),
                    RuntimeDependencies.Production() with { WebHostRunner = runner, ModelHttp = http,
                        ModelCredential = new IntentModelCredential(source), ModelClock = source.Clock }, token);
                return new(runner.Status) { Content = new StringContent(runner.Body, System.Text.Encoding.UTF8, "application/json") };
            }
            if (uri.Host == "management.azure.com" && models.Resources.ContainsKey(uri.AbsolutePath))
                return await modelHttp.SendAsync(request, token);
            return await sourceHttp.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { sourceHttp.Dispose(); modelHttp.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
