using Xunit;

namespace Dsf.Cli.Tests;

/// <summary>
/// `dsf new --dry-run --write-plan` must never write outside config/instances,
/// no matter what --product contains.
/// </summary>
public sealed class NewProductPathSafetyTests
{
    private static ScriptedTerminal PlainTerminal() =>
        new(new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false), []);

    [Fact]
    public async Task Write_plan_refuses_a_traversing_product_key()
    {
        var sandbox = Sandbox();
        var root = Path.Combine(sandbox, "repo");
        Directory.CreateDirectory(root);
        try
        {
            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "../evil", "--owner", "acme",
                    "--name-prefix", "evilprod", "--dry-run", "--write-plan",
                    "--config-root", root,
                ],
                CancellationToken.None,
                PlainTerminal());

            Assert.NotEqual(0, exitCode);
            Assert.Empty(Directory.GetFiles(sandbox, "*.json", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(sandbox);
        }
    }

    [Fact]
    public async Task Write_plan_refuses_an_absolute_path_product_key()
    {
        var sandbox = Sandbox();
        var root = Path.Combine(sandbox, "repo");
        var escape = Path.Combine(sandbox, "escaped");
        Directory.CreateDirectory(root);
        try
        {
            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", escape, "--owner", "acme",
                    "--name-prefix", "evilprod", "--dry-run", "--write-plan",
                    "--config-root", root,
                ],
                CancellationToken.None,
                PlainTerminal());

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(escape + ".json"));
            Assert.Empty(Directory.GetFiles(sandbox, "*.json", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(sandbox);
        }
    }

    [Fact]
    public async Task Write_plan_still_accepts_a_hyphenated_product_key()
    {
        var sandbox = Sandbox();
        var root = Path.Combine(sandbox, "repo");
        Directory.CreateDirectory(root);
        try
        {
            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "pets-cool-clinic2", "--owner", "acme",
                    "--dry-run", "--write-plan", "--config-root", root,
                ],
                CancellationToken.None,
                PlainTerminal());

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "config", "instances", "pets-cool-clinic2.json")));
        }
        finally
        {
            Cleanup(sandbox);
        }
    }

    private static string Sandbox()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "dsf-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        return sandbox;
    }

    private static void Cleanup(string sandbox)
    {
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
