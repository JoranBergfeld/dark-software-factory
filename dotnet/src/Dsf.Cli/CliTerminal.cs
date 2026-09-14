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

    Task<string?> PromptSecretAsync(string message, CancellationToken cancellationToken);
}

internal sealed class SystemCliTerminal : ICliTerminal
{
    private static readonly TerminalCapabilities Plain = new(
        IsInteractive: false,
        SupportsAnsi: false,
        SupportsEmoji: false);

    private SystemCliTerminal(TerminalCapabilities capabilities)
    {
        Capabilities = capabilities;
    }

    public TerminalCapabilities Capabilities { get; }

    public static SystemCliTerminal Detect() => Detect(
        () => Console.IsInputRedirected,
        () => Console.IsOutputRedirected,
        Environment.GetEnvironmentVariable);

    internal static SystemCliTerminal Detect(
        Func<bool> isInputRedirected,
        Func<bool> isOutputRedirected,
        Func<string, string?> getEnvironmentVariable)
    {
        try
        {
            var interactive = !isInputRedirected() && !isOutputRedirected();
            var term = getEnvironmentVariable("TERM") ?? string.Empty;
            var limited = string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);
            var noColor = !string.IsNullOrWhiteSpace(getEnvironmentVariable("NO_COLOR"));
            return new SystemCliTerminal(
                new TerminalCapabilities(
                    IsInteractive: interactive,
                    SupportsAnsi: interactive && !limited && !noColor,
                    SupportsEmoji: interactive && !limited));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or PlatformNotSupportedException)
        {
            // No usable console handle (detached process, service host, restricted env):
            // degrade to a plain, non-interactive terminal instead of crashing.
            return new SystemCliTerminal(Plain);
        }
    }

    public void WriteLine(string value) => Console.Out.WriteLine(value);

    public void WriteErrorLine(string value) => Console.Error.WriteLine(value);

    public string? Prompt(string message)
    {
        Console.Out.Write(message);
        return Console.ReadLine();
    }

    public Task<string?> PromptSecretAsync(string message, CancellationToken cancellationToken) =>
        PromptSecretAsync(
            message,
            () => Console.KeyAvailable,
            () => Console.ReadKey(intercept: true),
            Console.Out,
            cancellationToken);

    internal static async Task<string?> PromptSecretAsync(
        string message,
        Func<bool> keyAvailable,
        Func<ConsoleKeyInfo> readKey,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var input = new System.Text.StringBuilder();
        output.Write(message);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!keyAvailable())
                {
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                var key = readKey();
                if (key.Key == ConsoleKey.Enter)
                {
                    return input.ToString();
                }

                if (key.Key == ConsoleKey.Backspace && input.Length > 0)
                {
                    input.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    input.Append(key.KeyChar);
                }
            }
        }
        finally
        {
            output.WriteLine();
        }
    }
}
