using Dsf.Core.Instances;
using Xunit;

namespace Dsf.Core.Tests;

/// <summary>
/// A product key is caller-supplied text, so it must never be able to steer an
/// instance-definition path out of <c>config/instances</c>.
/// </summary>
public sealed class InstanceDefinitionPathSafetyTests
{
    [Theory]
    [InlineData("demo")]
    [InlineData("pets-cool-clinic2")]
    [InlineData("microbi")]
    public void Valid_product_keys_resolve_under_config_instances(string product)
    {
        var root = TempRoot();
        try
        {
            var path = InstanceDefinitions.PathFor(root, product);

            Assert.Equal(Path.Combine(root, "config", "instances", $"{product}.json"), path);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("nested/../../evil")]
    [InlineData("sub/evil")]
    [InlineData("sub\\evil")]
    [InlineData("/tmp/evil")]
    [InlineData("")]
    [InlineData("   ")]
    public void Unsafe_product_keys_are_rejected(string product)
    {
        var root = TempRoot();
        try
        {
            var exception = Assert.Throws<InstanceDefinitionException>(
                () => InstanceDefinitions.PathFor(root, product));

            Assert.Contains("product", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Write_refuses_a_traversing_product_key_and_writes_nothing()
    {
        var root = TempRoot();
        try
        {
            var definition = new InstanceDefinition
            {
                Product = new ProductSettings { Key = "../evil" },
                Runtime = new RuntimeSettings(),
                Governance = new GovernanceSettings { ConfidenceThreshold = 0.6 },
                GitHub = new GitHubSettings { Owner = "acme", Repository = "evil", Visibility = "private" },
                Azure = new AzureSettings
                {
                    Location = "swedencentral",
                    NamePrefix = "evilprod0000",
                    ResourceGroup = "rg-dsf-evil",
                    DeploymentName = "dsf-evil",
                    SreAgent = new SreAgentSettings
                    {
                        Name = "dsf-sre-evil",
                        ResourceGroup = "rg-dsf-sre-evil",
                    },
                },
                Status = new InstanceStatus
                {
                    State = InstanceState.Planned,
                    GeneratedAt = DateTimeOffset.UnixEpoch,
                },
            };

            Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Write(definition, root));
            Assert.False(File.Exists(Path.Combine(root, "config", "evil.json")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsf-core-tests", Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
