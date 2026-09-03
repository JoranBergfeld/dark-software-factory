namespace Dsf.Cli;

internal readonly record struct TerminalCapabilities(
    bool IsInteractive,
    bool SupportsAnsi,
    bool SupportsEmoji);

internal interface ICliTerminal
{
    TerminalCapabilities Capabilities { get; }

    void WriteLine(string value);

    void WriteErrorLine(string value);

    string? Prompt(string message);
}

internal sealed class SystemCliTerminal : ICliTerminal
{
    private SystemCliTerminal(TerminalCapabilities capabilities)
    {
        Capabilities = capabilities;
    }

    public TerminalCapabilities Capabilities { get; }

    public static SystemCliTerminal Detect()
    {
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
        var limited = string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);
        var noColor = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR"));
        return new SystemCliTerminal(
            new TerminalCapabilities(
                IsInteractive: interactive,
                SupportsAnsi: interactive && !limited && !noColor,
                SupportsEmoji: interactive && !limited));
    }

    public void WriteLine(string value) => Console.Out.WriteLine(value);

    public void WriteErrorLine(string value) => Console.Error.WriteLine(value);

    public string? Prompt(string message)
    {
        Console.Out.Write(message);
        return Console.ReadLine();
    }
}
