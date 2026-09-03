using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CliPresentationTests
{
    private const char Esc = '\u001b';
    private const string Hint = "💡";

    [Fact]
    public async Task Interactive_terminal_with_ansi_and_emoji_decorates_the_equivalent_command()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: true, SupportsEmoji: true),
            ["demo", ""]);

        var exitCode = await CliApplication.InvokeAsync(["new", "--dry-run"], CancellationToken.None, terminal);

        Assert.Equal(0, exitCode);
        Assert.Contains("\u001b[2m[dsf] \ud83d\udca1 equivalent: dsf new --dry-run\u001b[0m", terminal.Output);
    }

    [Fact]
    public async Task Interactive_terminal_without_color_keeps_emoji_and_drops_ansi()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: true),
            ["demo", ""]);

        var exitCode = await CliApplication.InvokeAsync(["new", "--dry-run"], CancellationToken.None, terminal);

        Assert.Equal(0, exitCode);
        Assert.Contains($"[dsf] {Hint} equivalent: dsf new --dry-run", terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Esc, terminal.Output);
    }

    [Fact]
    public async Task Limited_terminal_renders_plain_equivalent_command()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            ["demo", ""]);

        var exitCode = await CliApplication.InvokeAsync(["new", "--dry-run"], CancellationToken.None, terminal);

        Assert.Equal(0, exitCode);
        Assert.Contains("[dsf] equivalent: dsf new --dry-run", terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Esc, terminal.Output);
        Assert.DoesNotContain(Hint, terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirected_terminal_emits_no_ansi_or_emoji_even_when_capabilities_claim_support()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: false, SupportsAnsi: true, SupportsEmoji: true),
            []);

        var exitCode = await CliApplication.InvokeAsync(
            ["new", "--product", "demo", "--name-prefix", "demo", "--dry-run"],
            CancellationToken.None,
            terminal);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(Esc, terminal.Output);
        Assert.DoesNotContain(Hint, terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Esc, terminal.Error);
        Assert.DoesNotContain(Hint, terminal.Error, StringComparison.Ordinal);
    }
}
