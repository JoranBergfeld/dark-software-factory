using System.Text.Json;
using Dsf.Cli;
using Dsf.Core.Instances;
using Xunit;

namespace Dsf.Cli.Tests;

/// <summary>
/// `dsf new --dry-run --write-plan` persists a clean, versioned instance definition:
/// configuration only, no command logs, no secret values.
/// </summary>
public sealed class NewInstanceDefinitionTests
{
    private static ScriptedTerminal PlainTerminal() =>
        new(new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false), []);

    [Fact]
    public async Task Write_plan_persists_a_v1_definition_at_the_conventional_path()
    {
        var root = TempRoot();
        try
        {
            var terminal = PlainTerminal();
            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "paritydemo", "--owner", "acme",
                    "--name-prefix", "paritydemo", "--dry-run", "--write-plan",
                    "--config-root", root,
                ],
                CancellationToken.None,
                terminal);

            Assert.Equal(0, exitCode);

            var path = Path.Combine(root, "config", "instances", "paritydemo.json");
            Assert.True(File.Exists(path), $"expected a written instance definition at {path}");

            var definition = InstanceDefinitions.Read(path);
            Assert.Equal(1, definition.SchemaVersion);
            Assert.Equal("paritydemo", definition.Product.Key);
            Assert.Equal("dev", definition.Product.Environment);
            Assert.Equal("low", definition.Product.CreationMaturity);
            Assert.Equal("aca", definition.Runtime.Target);
            Assert.Equal("acme", definition.GitHub.Owner);
            Assert.Equal("paritydemo", definition.GitHub.Repository);
            Assert.Equal("private", definition.GitHub.Visibility);
            Assert.Equal("swedencentral", definition.Azure.Location);
            Assert.Equal("parityde0000", definition.Azure.NamePrefix);
            Assert.Equal("rg-dsf-paritydemo", definition.Azure.ResourceGroup);
            Assert.Equal("dsf-paritydemo", definition.Azure.DeploymentName);
            Assert.Equal(InstanceState.Planned, definition.Status.State);
            Assert.Contains(path, terminal.Output, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Written_definition_contains_no_command_logs_or_secret_values()
    {
        var root = TempRoot();
        try
        {
            await CliApplication.InvokeAsync(
                [
                    "new", "--product", "paritydemo", "--owner", "acme",
                    "--dry-run", "--write-plan", "--config-root", root,
                    "--owner-keyvault-uri", "https://kv-owner.vault.azure.net/",
                ],
                CancellationToken.None,
                PlainTerminal());

            var json = await File.ReadAllTextAsync(Path.Combine(root, "config", "instances", "paritydemo.json"));

            foreach (var forbidden in new[] { "\"steps\"", "\"plan\"", "\"spec\"", "gh repo create", "az deployment", "BEGIN RSA" })
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
            }

            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                ["schemaVersion", "product", "runtime", "governance", "github", "azure", "status"],
                document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Dry_run_without_write_plan_writes_nothing()
    {
        var root = TempRoot();
        Directory.Delete(root, recursive: true);
        try
        {
            var exitCode = await CliApplication.InvokeAsync(
                ["new", "--product", "paritydemo", "--owner", "acme", "--dry-run", "--config-root", root],
                CancellationToken.None,
                PlainTerminal());

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(root), $"dry-run without --write-plan must not create {root}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Write_plan_overwrites_a_legacy_manifest_only_after_regeneration()
    {
        var root = TempRoot();
        try
        {
            var path = Path.Combine(root, "config", "instances", "paritydemo.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, """{"spec": {"product": "paritydemo"}, "plan": {"steps": []}}""");

            Assert.Throws<InstanceDefinitionException>(() => InstanceDefinitions.Read(path));

            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "paritydemo", "--owner", "acme",
                    "--dry-run", "--write-plan", "--config-root", root,
                ],
                CancellationToken.None,
                PlainTerminal());

            Assert.Equal(0, exitCode);
            Assert.Equal(1, InstanceDefinitions.Read(path).SchemaVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Generated_instance_definitions_are_git_ignored()
    {
        var repoRoot = RepoRoot();
        var gitignore = File.ReadAllLines(Path.Combine(repoRoot, ".gitignore"))
            .Select(line => line.Trim())
            .ToArray();

        Assert.Contains(gitignore, line => line is "config/" or "config/instances/" or "config/instances/*.json");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".gitignore")))
        {
            dir = dir.Parent;
        }

        return (dir ?? throw new InvalidOperationException("Could not locate the repository root.")).FullName;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsf-cli-tests", Guid.NewGuid().ToString("N"));
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
