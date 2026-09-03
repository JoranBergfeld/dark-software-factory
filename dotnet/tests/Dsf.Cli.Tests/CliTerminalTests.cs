using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CliTerminalTests
{
    [Fact]
    public void Detect_reports_full_capabilities_for_an_attached_terminal()
    {
        var terminal = SystemCliTerminal.Detect(
            () => false,
            () => false,
            _ => null);

        Assert.True(terminal.Capabilities.IsInteractive);
        Assert.True(terminal.Capabilities.SupportsAnsi);
        Assert.True(terminal.Capabilities.SupportsEmoji);
    }

    [Fact]
    public void Detect_disables_prompts_and_features_when_output_is_redirected()
    {
        var terminal = SystemCliTerminal.Detect(
            () => false,
            () => true,
            _ => null);

        Assert.False(terminal.Capabilities.IsInteractive);
        Assert.False(terminal.Capabilities.SupportsAnsi);
        Assert.False(terminal.Capabilities.SupportsEmoji);
    }

    [Fact]
    public void Detect_disables_features_for_a_limited_terminal()
    {
        var terminal = SystemCliTerminal.Detect(
            () => false,
            () => false,
            name => name == "TERM" ? "dumb" : null);

        Assert.True(terminal.Capabilities.IsInteractive);
        Assert.False(terminal.Capabilities.SupportsAnsi);
        Assert.False(terminal.Capabilities.SupportsEmoji);
    }

    [Fact]
    public void Detect_disables_ansi_but_keeps_emoji_when_color_is_suppressed()
    {
        var terminal = SystemCliTerminal.Detect(
            () => false,
            () => false,
            name => name == "NO_COLOR" ? "1" : null);

        Assert.True(terminal.Capabilities.IsInteractive);
        Assert.False(terminal.Capabilities.SupportsAnsi);
        Assert.True(terminal.Capabilities.SupportsEmoji);
    }

    [Fact]
    public void Detect_degrades_safely_when_console_redirection_checks_throw()
    {
        var terminal = SystemCliTerminal.Detect(
            () => throw new IOException("no console handle"),
            () => throw new IOException("no console handle"),
            _ => null);

        Assert.False(terminal.Capabilities.IsInteractive);
        Assert.False(terminal.Capabilities.SupportsAnsi);
        Assert.False(terminal.Capabilities.SupportsEmoji);
    }

    [Fact]
    public void Detect_degrades_safely_when_environment_lookup_throws()
    {
        var terminal = SystemCliTerminal.Detect(
            () => false,
            () => false,
            _ => throw new System.Security.SecurityException("environment is not accessible"));

        Assert.False(terminal.Capabilities.IsInteractive);
        Assert.False(terminal.Capabilities.SupportsAnsi);
        Assert.False(terminal.Capabilities.SupportsEmoji);
    }
}
