using System.CommandLine;
using System.Text;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

/// <summary>
/// Locks the frozen command/option/alias grammar down to an exact, ordered snapshot
/// (rather than substring "Contains" checks), so an accidental addition, removal,
/// reordering, or alias change to the parity surface fails this test.
/// </summary>
public sealed class CliCommandSurfaceTests
{
    [Fact]
    public void Full_command_surface_matches_frozen_snapshot()
    {
        var root = CliApplication.BuildRootCommand();

        var actual = Describe(root, 0);

        Assert.Equal(ExpectedSnapshot.Trim('\n').Replace("\r\n", "\n"), actual.TrimEnd('\n').Replace("\r\n", "\n"));
    }

    private static string Describe(Command command, int depth)
    {
        var indent = new string(' ', depth * 2);
        var sb = new StringBuilder();

        sb.Append(indent).Append("command ").Append(depth == 0 ? "<root>" : command.Name);
        AppendAliases(sb, command.Aliases);
        sb.Append('\n');

        foreach (var argument in command.Arguments)
        {
            sb.Append(indent).Append("  argument ").Append(argument.Name).Append('\n');
        }

        foreach (var option in command.Options)
        {
            sb.Append(indent).Append("  option ").Append(option.Name);
            AppendAliases(sb, option.Aliases.Where(alias => alias != option.Name));
            if (option.Required)
            {
                sb.Append(" required");
            }

            sb.Append('\n');
        }

        foreach (var subcommand in command.Subcommands)
        {
            sb.Append(Describe(subcommand, depth + 1));
        }

        return sb.ToString();
    }

    private static void AppendAliases(StringBuilder sb, IEnumerable<string> aliases)
    {
        var list = aliases.ToArray();
        if (list.Length == 0)
        {
            return;
        }

        sb.Append(" aliases=[").Append(string.Join(", ", list)).Append(']');
    }

    private const string ExpectedSnapshot = """
command <root>
  option --help aliases=[-h]
  command new
    option --product required
    option --owner
    option --repo
    option --visibility
    option --runtime-target
    option --name-prefix
    option --environment
    option --location
    option --creation-maturity
    option --dry-run
    option --no-charter
    option --write-plan
    option --config-root
    option --owner-keyvault-uri
    option --owner-appconfig-endpoint
    option --admin-principal-id
  command list aliases=[ls]
    option --json
    option --owner-appconfig-endpoint
  command offboard
    argument product
    option --dry-run
    option --yes
    option --purge
    option --config-root
    option --owner-appconfig-endpoint
  command bootstrap
    option --app-name required
    option --keyvault-name required
    option --appconfig-name required
    option --resource-group
    option --location
  command delete
    argument product
    option --yes
    option --dry-run
    option --purge
    option --config-root
    option --owner-appconfig-endpoint
  command deprovision
    argument product
    option --yes
    option --dry-run
    option --purge
    option --config-root
    option --owner-appconfig-endpoint
  command run
    option --signal
    option --dry-run
    option --product
  command sweep
    option --product
  command serve-orchestrator
    option --loop
    option --interval
    option --product
  command serve-agent
    option --kind
    option --host
    option --port
  command charter
    command init
      option --product required
    command implement
      option --product required
      option --no-wait
      option --timeout
      option --poll-interval
    command watch
      option --product required
      option --issue
      option --timeout
      option --poll-interval
    command sync
      option --product required
      option --file
      option --ref
    command status
      option --product required
      option --file
      option --ref
""";
}
