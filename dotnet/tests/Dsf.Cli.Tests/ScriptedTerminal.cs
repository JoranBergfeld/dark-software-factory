using Dsf.Cli;

namespace Dsf.Cli.Tests;

internal sealed class ScriptedTerminal(TerminalCapabilities capabilities, IReadOnlyList<string> answers)
    : ICliTerminal
{
    private readonly Queue<string> _answers = new(answers);
    private readonly StringWriter _error = new();
    private readonly StringWriter _output = new();
    private readonly List<string> _prompts = [];

    public TerminalCapabilities Capabilities { get; } = capabilities;
    public IReadOnlyList<string> Prompts => _prompts;
    public string Output => _output.ToString();
    public string Error => _error.ToString();

    public void WriteLine(string value) => _output.WriteLine(value);

    public void WriteErrorLine(string value) => _error.WriteLine(value);

    public string? Prompt(string message)
    {
        _prompts.Add(message);
        return _answers.Count == 0 ? null : _answers.Dequeue();
    }
}
