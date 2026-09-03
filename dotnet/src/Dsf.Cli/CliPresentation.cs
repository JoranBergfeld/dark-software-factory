namespace Dsf.Cli;

/// <summary>
/// Presentation seam: turns CLI notices into text that honours the terminal's capabilities.
/// ANSI styling and emoji are opt-in per capability; everything degrades to plain ASCII.
/// </summary>
internal static class CliPresentation
{
    private const string Dim = "\u001b[2m";
    private const string Reset = "\u001b[0m";
    private const string HintEmoji = "💡";

    public static string EquivalentCommand(TerminalCapabilities capabilities, string command)
    {
        var emoji = capabilities.IsInteractive && capabilities.SupportsEmoji;
        var ansi = capabilities.IsInteractive && capabilities.SupportsAnsi;
        var label = emoji ? $"[dsf] {HintEmoji} equivalent" : "[dsf] equivalent";
        var line = $"{label}: {command}";
        return ansi ? $"{Dim}{line}{Reset}" : line;
    }
}
