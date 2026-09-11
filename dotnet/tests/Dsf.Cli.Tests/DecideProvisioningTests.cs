using Dsf.Cli;
using Dsf.Core.Instances;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class DecideProvisioningTests
{
    [Fact]
    public async Task New_persists_and_reuses_explicit_source_configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsf-decide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "decide.json");
            await File.WriteAllTextAsync(path, """
                {
                  "enabledSourceAgentKinds": ["azuremonitor"],
                  "azureMonitorWorkspaceId": "workspace-id",
                  "azureMonitorQuery": "AppExceptions | take 20",
                  "juryModels": [
                    {"provider":"openai","family":"gpt","endpoint":"https://jury.example","deployment":"gpt"},
                    {"provider":"deepseek","family":"deepseek","endpoint":"https://jury.example","deployment":"deepseek"},
                    {"provider":"xai","family":"grok","endpoint":"https://jury.example","deployment":"grok"}
                  ],
                  "deliberationRounds": 1,
                  "lenses": [{"name":"cost","weight":0.5}]
                }
                """);
            var terminal = Terminal();
            string[] args =
            [
                "new", "--product", "demo", "--owner", "acme", "--dry-run", "--write-plan",
                "--config-root", root,
            ];

            Assert.Equal(0, await CliApplication.InvokeAsync(
                [.. args, "--decide-config", path], CancellationToken.None, terminal));
            Assert.Equal(0, await CliApplication.InvokeAsync(args, CancellationToken.None, terminal));

            var definition = InstanceDefinitions.Read(InstanceDefinitions.PathFor(root, "demo"));
            Assert.Equal(definition, InstanceDefinitions.Parse(InstanceDefinitions.Serialize(definition), "roundtrip"));
            var topology = Assert.Single(AzureProvisioningPlan.Build(definition, root)
                .Requests.OfType<DeployTopologyRequest>());
            Assert.Equal(["azuremonitor"], topology.Decide.EnabledSourceAgentKinds);
            Assert.Equal("workspace-id", topology.Decide.AzureMonitorWorkspaceId);
            Assert.Equal("AppExceptions | take 20", topology.Decide.AzureMonitorQuery);
            Assert.Equal(3, topology.Decide.JuryModels.Count);
            Assert.Equal(1, topology.Decide.DeliberationRounds);
            Assert.Equal(0.5, Assert.Single(topology.Decide.Lenses).Weight);
            var index = CliApplication.RuntimeIndexValues(definition, "https://appconfig.example");
            Assert.Equal(3, JurySettings.Read(index.ToDictionary(p => p.Key, p => (string?)p.Value)).Jurors.Count);
            Assert.Equal("1", index[DeliberationSettings.RoundsKey]);
            Assert.Contains("azuremonitor", terminal.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"enabledSourceAgentKinds":["sentry"]}""", "sentry")]
    [InlineData("""{"enabledSourceAgentKinds":["azuremonitor"]}""", "azureMonitorWorkspaceId")]
    [InlineData("""{"enabledSourceAgentKinds":["webiq"],"webIqQuery":"market","apiKey":"not-allowed"}""", "apiKey")]
    [InlineData("""{"enabledSourceAgentKinds":null}""", "enabledSourceAgentKinds")]
    [InlineData("""{"enabledSourceAgentKinds":["webiq"],"webIqQuery":"market"}""", "DSF_JURY_MODELS")]
    [InlineData("""{"enabledSourceAgentKinds":[],"deliberationRounds":3}""", "DSF_DELIBERATION_ROUNDS")]
    [InlineData("""{"enabledSourceAgentKinds":[],"juryModels":null}""", "juryModels")]
    [InlineData("""{"enabledSourceAgentKinds":["foundryiq"],"foundryIqSearchEndpoint":"https://foundry.example/api/projects/demo","foundryIqKnowledgeBase":"kb","foundryIqQuery":"needs"}""", "foundryIqSearchEndpoint")]
    public async Task Invalid_configuration_fails_before_any_provisioning(string json, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var github = new RecordingGitHubProvisioningClient();
            var azure = new RecordingAzureProvisioningClient();
            var terminal = Terminal();

            var exit = await CliApplication.InvokeAsync(
                ["new", "--product", "demo", "--owner", "acme", "--decide-config", path],
                CancellationToken.None, terminal, github, azure);

            Assert.NotEqual(0, exit);
            Assert.Contains(expected, terminal.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(azure.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Topology_passes_only_configured_source_parameters()
    {
        var bicep = Path.GetTempFileName();
        try
        {
            var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "{}", ""));
            var client = new AzureCliProvisioningClient(runner);
            var request = new DeployTopologyRequest(
                "rg-demo", "demo", bicep, "demo", "dev", "swedencentral", "demo", "image",
                "1", "2", "acme/demo", true, null)
            {
                Decide = new DecideDeploymentSettings
                {
                    EnabledSourceAgentKinds = ["azuremonitor"],
                    AzureMonitorWorkspaceId = "workspace-id",
                    AzureMonitorQuery = "AppExceptions | take 20",
                    JuryModels = Models(),
                    Lenses = [new DeliberationLensSettings(" Cost ", Weight: 0.5)],
                },
            };

            await client.DeployTopologyAsync(request, CancellationToken.None);

            var invocation = Assert.Single(runner.Invocations);
            Assert.Contains("enabledSourceAgentKinds=[\"azuremonitor\"]", invocation);
            Assert.Contains("azureMonitorWorkspaceId=workspace-id", invocation);
            Assert.Contains("azureMonitorQuery=AppExceptions | take 20", invocation);
            Assert.Contains(invocation, value => value.StartsWith("juryModels=[", StringComparison.Ordinal));
            var lenses = Assert.Single(invocation, value =>
                value.StartsWith("deliberationLenses=", StringComparison.Ordinal));
            Assert.Contains("\"name\":\"cost\"", lenses, StringComparison.Ordinal);
            Assert.DoesNotContain(" Cost ", lenses, StringComparison.Ordinal);
            Assert.DoesNotContain(invocation, value => value.StartsWith("webIqQuery=", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(bicep);
        }
    }

    [Fact]
    public void Saved_instance_rejects_a_null_decide_configuration()
    {
        var definition = PlannedInstanceDefinition.Build(
            "demo", "acme", "demo", "private", "aca", "dev", "swedencentral",
            "low", "low", "demo", null, null, null, null, null, DateTimeOffset.UnixEpoch);
        var json = System.Text.Json.Nodes.JsonNode.Parse(InstanceDefinitions.Serialize(definition))!;
        json["runtime"]!["decide"] = null;

        var error = Assert.Throws<InstanceDefinitionException>(
            () => InstanceDefinitions.Parse(json.ToJsonString(), "instance.json"));

        Assert.Contains("decide", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ScriptedTerminal Terminal() =>
        new(new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false), []);

    private static IReadOnlyList<JurorModelSettings> Models() =>
    [
        new("one", "openai", "gpt", "https://jury.example", "gpt"),
        new("two", "deepseek", "deepseek", "https://jury.example", "deepseek"),
        new("three", "xai", "grok", "https://jury.example", "grok"),
    ];
}
