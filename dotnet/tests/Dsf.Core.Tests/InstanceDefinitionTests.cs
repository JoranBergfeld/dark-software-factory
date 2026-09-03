using System.Text.Json;
using Dsf.Core.Instances;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class InstanceDefinitionTests
{
    private static InstanceDefinition Sample() => new()
    {
        Product = new ProductSettings
        {
            Key = "paritydemo",
            Environment = "dev",
            CreationMaturity = "low",
        },
        Runtime = new RuntimeSettings
        {
            Target = "aca",
            Image = "ghcr.io/joranbergfeld/dsf-runtime:latest",
        },
        Governance = new GovernanceSettings
        {
            ConfidenceThreshold = 0.6,
            LabelTaxonomy = GovernanceSettings.DefaultLabelTaxonomy,
        },
        GitHub = new GitHubSettings
        {
            Owner = "acme",
            Repository = "paritydemo",
            Visibility = "private",
            PrivateKeySecretName = "github-app-private-key",
        },
        Azure = new AzureSettings
        {
            Location = "swedencentral",
            NamePrefix = "parityde0000",
            ResourceGroup = "rg-dsf-paritydemo",
            DeploymentName = "dsf-paritydemo",
            SreAgent = new SreAgentSettings
            {
                Name = "dsf-sre-paritydemo",
                ResourceGroup = "rg-dsf-sre-paritydemo",
                Location = "swedencentral",
                MonitoredResourceGroups = ["rg-dsf-paritydemo"],
            },
        },
        Status = new InstanceStatus
        {
            State = InstanceState.Planned,
            GeneratedAt = DateTimeOffset.UnixEpoch,
        },
    };

    [Fact]
    public void Serialized_definition_uses_schema_version_1_and_the_clean_shape()
    {
        var json = InstanceDefinitions.Serialize(Sample());

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ["schemaVersion", "product", "runtime", "governance", "github", "azure", "status"],
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("paritydemo", root.GetProperty("product").GetProperty("key").GetString());
        Assert.Equal("aca", root.GetProperty("runtime").GetProperty("target").GetString());
        Assert.Equal(0.6, root.GetProperty("governance").GetProperty("confidenceThreshold").GetDouble());
        Assert.Equal("acme", root.GetProperty("github").GetProperty("owner").GetString());
        Assert.Equal("rg-dsf-paritydemo", root.GetProperty("azure").GetProperty("resourceGroup").GetString());
        Assert.Equal("planned", root.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public void Serialized_definition_carries_no_command_logs_or_secret_values()
    {
        var json = InstanceDefinitions.Serialize(Sample());

        foreach (var forbidden in new[] { "\"steps\"", "\"plan\"", "\"spec\"", "\"command\"", "\"commands\"", "privateKey\"", "BEGIN RSA" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }

        Assert.Contains("privateKeySecretName", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trips_through_write_and_read()
    {
        var root = TempRoot();
        try
        {
            var path = InstanceDefinitions.Write(Sample(), root);

            Assert.Equal(Path.Combine(root, "config", "instances", "paritydemo.json"), path);

            var read = InstanceDefinitions.Read(path);
            Assert.Equal(Sample(), read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unknown_schema_version_fails_with_regeneration_guidance()
    {
        var json = """{"schemaVersion": 2, "product": {"key": "demo"}}""";

        var error = Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Parse(json, "demo.json"));

        Assert.Contains("schemaVersion 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("demo.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("dsf new", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_execution_manifest_fails_with_regeneration_guidance()
    {
        var json = """
        {"spec": {"product": "demo", "owner": "acme"}, "plan": {"product": "demo", "steps": []}, "executed": false}
        """;

        var error = Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Parse(json, "demo.json"));

        Assert.Contains("legacy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("demo.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("dsf new", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_schema_version_fails_with_regeneration_guidance()
    {
        var json = """{"product": {"key": "demo"}}""";

        var error = Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Parse(json, "demo.json"));

        Assert.Contains("schemaVersion", error.Message, StringComparison.Ordinal);
        Assert.Contains("dsf new", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_fails_with_regeneration_guidance()
    {
        var error = Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Parse("{not json", "demo.json"));

        Assert.Contains("demo.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("dsf new", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_a_missing_definition_fails_with_regeneration_guidance()
    {
        var path = Path.Combine(TempRootName(), "config", "instances", "ghost.json");

        var error = Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Read(path));

        Assert.Contains("ghost.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("dsf new", error.Message, StringComparison.Ordinal);
    }

    private static string TempRootName() =>
        Path.Combine(Path.GetTempPath(), "dsf-instance-tests", Guid.NewGuid().ToString("N"));

    private static string TempRoot()
    {
        var root = TempRootName();
        Directory.CreateDirectory(root);
        return root;
    }
}
