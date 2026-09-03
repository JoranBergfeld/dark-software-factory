using System.CommandLine;
using System.CommandLine.Completions;
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
        AppendDescription(sb, command.Description);
        sb.Append('\n');

        foreach (var argument in command.Arguments)
        {
            sb.Append(indent).Append("  argument ").Append(argument.Name);
            AppendValueShape(sb, argument.ValueType, argument.Arity);
            AppendDescription(sb, argument.Description);
            sb.Append('\n');
        }

        foreach (var option in command.Options)
        {
            sb.Append(indent).Append("  option ").Append(option.Name);
            AppendAliases(sb, option.Aliases.Where(alias => alias != option.Name));
            AppendValueShape(sb, option.ValueType, option.Arity);
            if (option.HasDefaultValue)
            {
                sb.Append(" default=").Append(FormatValue(option.GetDefaultValue()));
            }

            var choices = option.GetCompletions(CompletionContext.Empty)
                .Select(completion => completion.Label)
                .Where(label => !option.Aliases.Contains(label))
                .ToArray();
            if (choices.Length > 0)
            {
                sb.Append(" choices=[").Append(string.Join(", ", choices)).Append(']');
            }

            if (option.Required)
            {
                sb.Append(" required");
            }

            AppendDescription(sb, option.Description);
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

    private static void AppendValueShape(StringBuilder sb, Type valueType, ArgumentArity arity) =>
        sb.Append(" type=")
            .Append(FriendlyTypeName(valueType))
            .Append(" arity=")
            .Append(arity.MinimumNumberOfValues)
            .Append("..")
            .Append(arity.MaximumNumberOfValues);

    private static void AppendDescription(StringBuilder sb, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        sb.Append(" desc=").Append(FormatValue(description));
    }

    private static string FriendlyTypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        return underlying is null ? type.Name : underlying.Name + "?";
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "<null>",
            string text => $"\"{text}\"",
            _ => value.ToString() ?? string.Empty,
        };

    private const string ExpectedSnapshot = """
command <root> desc="Dark Software Factory — factory CLI (create product instances)"
  option --help aliases=[-h] type=Void arity=0..0 desc="Show help and usage information"
  command new desc="create a new isolated product factory instance"
    option --product type=String arity=1..1 desc="product key (e.g. 'microbi')"
    option --owner type=String arity=1..1 default="" desc="GitHub owner/org for the product repo"
    option --repo type=String arity=1..1 default="" desc="repo name (defaults to product key)"
    option --visibility type=String arity=1..1 default="private" choices=[internal, private, public] desc="product repo visibility"
    option --runtime-target type=String arity=1..1 default="aca" choices=[aca] desc="where the factory runtime is hosted"
    option --name-prefix type=String arity=1..1 default="" desc="base Azure resource name prefix"
    option --environment type=String arity=1..1 default="dev" desc="Azure environment moniker"
    option --location type=String arity=1..1 default="swedencentral" desc="Azure region"
    option --creation-maturity type=String arity=1..1 default="low" choices=[high, low] desc="creation-phase autonomy"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="preview only: print the what-if plan without running steps"
    option --no-charter type=Boolean arity=0..1 default=False choices=[False, True] desc="skip the post-provision charter prompt"
    option --write-plan type=Boolean arity=0..1 default=False choices=[False, True] desc="with --dry-run, still write the instance manifest"
    option --config-root type=String arity=1..1 desc="override repo root where config/instances/ is written"
    option --owner-keyvault-uri type=String arity=1..1 default="" desc="owner Key Vault URI"
    option --owner-appconfig-endpoint type=String arity=1..1 desc="owner App Configuration endpoint"
    option --admin-principal-id type=String arity=1..1 default="" desc="human owner/governance principal object id"
  command list aliases=[ls] desc="list provisioned product factories from the owner App Config index"
    option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="emit the factory rows as JSON for scripting"
    option --owner-appconfig-endpoint type=String arity=1..1 desc="owner App Configuration endpoint"
  command offboard desc="remove Azure/runtime artifacts for a product"
    argument product type=String arity=1..1 desc="product key to offboard"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="preview only: print the teardown plan without side effects"
    option --yes type=Boolean arity=0..1 default=False choices=[False, True] desc="skip interactive confirmation for destructive delete steps"
    option --purge type=Boolean arity=0..1 default=False choices=[False, True] desc="also purge soft-deleted resources for name reuse"
    option --config-root type=String arity=1..1 desc="override repo root where config/instances/ lives"
    option --owner-appconfig-endpoint type=String arity=1..1 desc="owner App Configuration endpoint"
  command bootstrap desc="one-time: create the DSF GitHub App and store it in the owner Key Vault"
    option --app-name type=String arity=1..1 required desc="GitHub App name"
    option --keyvault-name type=String arity=1..1 required desc="owner Key Vault name for App credentials"
    option --appconfig-name type=String arity=1..1 required desc="owner App Configuration store name"
    option --resource-group type=String arity=1..1 default="rg-dsf-app" desc="resource group for the owner Key Vault"
    option --location type=String arity=1..1 default="swedencentral" desc="Azure region for the owner Key Vault"
  command delete desc="permanently destroy a product factory instance"
    argument product type=String arity=1..1 desc="product key to destroy"
    option --yes type=Boolean arity=0..1 default=False choices=[False, True] desc="skip the interactive confirmation prompt"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="preview only: print the full teardown plan"
    option --purge type=Boolean arity=0..1 default=False choices=[False, True] desc="purge soft-deleted resources for name reuse"
    option --config-root type=String arity=1..1 desc="override repo root where config/instances/ is read"
    option --owner-appconfig-endpoint type=String arity=1..1 desc="owner App Configuration endpoint"
  command deprovision desc="permanently destroy a product factory instance"
    argument product type=String arity=1..1 desc="product key to destroy"
    option --yes type=Boolean arity=0..1 default=False choices=[False, True] desc="skip the interactive confirmation prompt"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="preview only: print the full teardown plan"
    option --purge type=Boolean arity=0..1 default=False choices=[False, True] desc="purge soft-deleted resources for name reuse"
    option --config-root type=String arity=1..1 desc="override repo root where config/instances/ is read"
    option --owner-appconfig-endpoint type=String arity=1..1 desc="owner App Configuration endpoint"
  command run desc="run the intake line for one signal (runtime)"
    option --signal type=String arity=1..1 desc="path to a signal JSON file"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="run the line but skip filing"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command sweep desc="sweep enabled source agents once (runtime)"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command serve-orchestrator desc="run the orchestrator worker (runtime)"
    option --loop type=Boolean arity=0..1 default=False choices=[False, True] desc="sweep continuously"
    option --interval type=Int32? arity=1..1 desc="seconds between sweeps"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command serve-agent desc="serve a source agent over A2A (runtime)"
    option --kind type=String arity=1..1 default="sentry" desc="source agent kind"
    option --host type=String arity=1..1 default="0.0.0.0" desc="bind host"
    option --port type=Int32? arity=1..1 default=8080 desc="bind port"
  command charter desc="manage the product charter (.dsf/charter.md)"
    command init desc="interview to draft a charter and open a PR"
      option --product type=String arity=1..1 required desc="product key"
    command implement desc="render the constitution + file the Spec Kit bootstrap issue"
      option --product type=String arity=1..1 required desc="product key"
      option --no-wait type=Boolean arity=0..1 default=False choices=[False, True] desc="file + assign only; do not watch"
      option --timeout type=Double? arity=1..1 desc="max seconds to wait"
      option --poll-interval type=Double? arity=1..1 desc="seconds between polls"
    command watch desc="watch the coding agent's build and request Copilot review when ready"
      option --product type=String arity=1..1 required desc="product key"
      option --issue type=Int32? arity=1..1 desc="bootstrap issue number"
      option --timeout type=Double? arity=1..1 desc="max seconds to watch"
      option --poll-interval type=Double? arity=1..1 desc="seconds between polls"
    command sync desc="pull .dsf/charter.md (local file or --ref) into Cosmos"
      option --product type=String arity=1..1 required desc="product key"
      option --file type=String arity=1..1 desc="path to a local charter file"
      option --ref type=String arity=1..1 desc="read the charter from this repo ref via the GitHub App"
    command status desc="show the stored charter status + drift"
      option --product type=String arity=1..1 required desc="product key"
      option --file type=String arity=1..1 desc="path to a local charter file"
      option --ref type=String arity=1..1 desc="read the charter from this repo ref via the GitHub App"
""";
}
