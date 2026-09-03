using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CliInteractionTests
{
    [Fact]
    public async Task New_interactive_flow_asks_one_question_at_a_time_and_shows_equivalent_command()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            ["demo"]);

        var exitCode = await CliApplication.InvokeAsync(["new", "--dry-run"], CancellationToken.None, terminal);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Product key: "], terminal.Prompts);
        Assert.Contains("[dsf] equivalent: dsf new --dry-run", terminal.Output);
        Assert.Contains("[dsf] equivalent: dsf new --product demo --dry-run", terminal.Output);
        Assert.Contains("[dsf] instance plan for product=demo (DRY-RUN)", terminal.Output);
    }

    [Fact]
    public async Task New_equivalent_command_includes_every_explicit_option()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            ["demo"]);

        var exitCode = await CliApplication.InvokeAsync(
            [
                "new",
                "--owner",
                "acme",
                "--repo",
                "demo-repo",
                "--visibility",
                "public",
                "--creation-maturity",
                "high",
                "--dry-run",
                "--config-root",
                "/tmp/root",
            ],
            CancellationToken.None,
            terminal);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Product key: "], terminal.Prompts);
        Assert.Contains(
            "[dsf] equivalent: dsf new --product demo --owner acme --repo demo-repo --visibility public"
                + " --creation-maturity high --dry-run --config-root /tmp/root",
            terminal.Output);
    }

    [Fact]
    public async Task New_does_not_prompt_for_name_prefix_when_product_supplies_the_default()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            []);

        var exitCode = await CliApplication.InvokeAsync(
            ["new", "--product", "demo", "--dry-run"],
            CancellationToken.None,
            terminal);

        Assert.Equal(0, exitCode);
        Assert.Empty(terminal.Prompts);
        Assert.DoesNotContain("[dsf] equivalent:", terminal.Output);
        Assert.Contains("[dsf] instance plan for product=demo (DRY-RUN)", terminal.Output);
    }

    [Fact]
    public async Task New_with_explicit_arguments_bypasses_interactive_prompts()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            []);

        var exitCode = await CliApplication.InvokeAsync(
            [
                "new",
                "--product",
                "demo",
                "--name-prefix",
                "demo",
                "--location",
                "westeurope",
                "--environment",
                "test",
                "--dry-run",
            ],
            CancellationToken.None,
            terminal);

        Assert.Equal(0, exitCode);
        Assert.Empty(terminal.Prompts);
        Assert.DoesNotContain("[dsf] equivalent:", terminal.Output);
        Assert.Contains("[dsf] instance plan for product=demo (DRY-RUN)", terminal.Output);
    }

    [Fact]
    public async Task New_missing_required_prompt_answer_fails_without_terminal_features_when_redirected()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: false, SupportsAnsi: true, SupportsEmoji: true),
            []);

        var exitCode = await CliApplication.InvokeAsync(["new", "--dry-run"], CancellationToken.None, terminal);

        Assert.Equal(1, exitCode);
        Assert.Empty(terminal.Prompts);
        Assert.Equal(string.Empty, terminal.Output);
        Assert.Equal(
            "[dsf] error: --product is required when prompts are unavailable. Run: dsf new --product <product> --dry-run"
                + Environment.NewLine,
            terminal.Error);
        Assert.DoesNotContain('\u001b', terminal.Error);
        Assert.DoesNotContain("⚠", terminal.Error);
    }
}
