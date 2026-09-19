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
    option --creation-maturity type=String arity=1..1 default="low" choices=[high, low, medium] desc="creation-phase autonomy"
    option --operation-maturity type=String arity=1..1 default="low" choices=[high, low, medium] desc="operation-phase autonomy"
    option --decide-config type=String arity=1..1 desc="nonsecret Decide source and judgment configuration JSON"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="preview only: print the what-if plan without running steps"
    option --no-charter type=Boolean arity=0..1 default=False choices=[False, True] desc="skip the post-provision charter prompt"
    option --write-plan type=Boolean arity=0..1 default=False choices=[False, True] desc="with --dry-run, still write the instance manifest"
    option --config-root type=String arity=1..1 desc="override instance output root where config/instances/ is written"
    option --owner-keyvault-uri type=String arity=1..1 default="" desc="owner Key Vault URI"
    option --owner-appconfig-endpoint type=String arity=1..1 desc="owner App Configuration endpoint"
    option --admin-principal-id type=String arity=1..1 default="" desc="human owner/governance principal object id"
    option --github-app-id type=String arity=1..1 default="" desc="owner DSF GitHub App id"
    option --github-installation-id type=String arity=1..1 default="" desc="owner DSF GitHub App installation id"
    option --github-installation-selection type=String arity=1..1 default="" choices=[all, selected] desc="owner DSF GitHub App installation selection"
  command onboard desc="stage existing application attachment and inactive foundation without greenfield provisioning"
    command decide desc="assess attachment of an existing application to Decide"
      command review desc="read-only repository attachment assessment; does not onboard or activate"
        option --repo type=String arity=1..1 desc="existing GitHub.com repository (OWNER/REPO)"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="prepared owner App Configuration endpoint (or DSF_OWNER_APPCONFIG_ENDPOINT)"
        option --owner-keyvault-uri type=String arity=1..1 desc="prepared owner Key Vault URI (or DSF_OWNER_KEYVAULT_URI)"
        option --issue-automation type=String arity=1..1 default="unknown" choices=[clear, conflict, unknown] desc="external-owner declaration for proposal issues"
        option --pr-automation type=String arity=1..1 default="unknown" choices=[clear, conflict, unknown] desc="external-owner declaration for charter PRs"
        option --label-automation type=String arity=1..1 default="unknown" choices=[clear, conflict, unknown] desc="external-owner declaration for label creation"
        option --automation-owner type=String arity=1..1 desc="accountable external automation owner/team; required for clear/conflict declarations"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="print typed, nonsecret repository-review facts as JSON"
      command plan desc="preview a dedicated application boundary; never reserve, provision or activate"
        option --input type=String arity=1..1 desc="complete versioned application selection JSON file"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="print the read-only categorized application boundary plan"
        option --reviewed-fingerprint type=String arity=1..1 desc="reject replay unless the fresh categorized plan matches this fingerprint"
      command provision desc="provision only the approved inactive foundation; never activate"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner reservation endpoint"
        option --product type=String arity=1..1 desc="reserved product key"
        option --tenant type=String arity=1..1 desc="reserved tenant ID"
        option --subscription type=String arity=1..1 desc="reserved subscription ID"
        option --reviewed-fingerprint type=String arity=1..1 desc="separate approval of the reserved full plan for INACTIVE foundation provisioning"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="nonsecret foundation state; never prompt"
      command github desc="prepare and verify only the approved factory GitHub authority"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner reservation endpoint"
        option --product type=String arity=1..1 desc="reserved product key"
        option --tenant type=String arity=1..1 desc="reserved tenant ID"
        option --subscription type=String arity=1..1 desc="reserved subscription ID"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret authority result; never prompt"
        option --source-vault-id type=String arity=1..1 desc="exact existing owner vault ARM ID, including its subscription"
        option --source-key-version type=String arity=1..1 desc="approved existing owner signing key version"
        option --runtime-image type=String arity=1..1 desc="implemented DSF runtime image pinned by sha256 digest"
        option --auth-audience type=String arity=1..1 desc="existing single-tenant API application client ID for workload authentication"
        option --approve type=String arity=1..1 desc="exact authority preview fingerprint; accepts disclosed broader signing risk"
        option --verify-only type=Boolean arity=0..1 default=False choices=[False, True] desc="refresh deployed proof only; never retry key copies, grants or deployments"
      command telemetry desc="prepare and check isolated application telemetry; never activate"
        option --freshness-hours type=String arity=1..1 desc="reviewed positive freshness window; default 24 hours"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured result; never prompt"
        option --runtime-image type=String arity=1..1 desc="implemented runtime image pinned by sha256"
        option --auth-audience type=String arity=1..1 desc="existing approved Entra API client ID"
        option --approve type=String arity=1..1 desc="exact displayed telemetry configuration fingerprint"
        option --verify-only type=Boolean arity=0..1 default=False choices=[False, True] desc="hold readiness then recheck existing grants, host and actual workload; no creation retry"
      command supplemental desc="explicit optional evidence sources; never replace application telemetry or activate"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured result; never prompt"
        option --input type=String arity=1..1 desc="exact reviewed optional source selection JSON; never contains a secret value"
        option --approve type=String arity=1..1 desc="exact displayed supplemental selection fingerprint"
        option --verify-only type=Boolean arity=0..1 default=False choices=[False, True] desc="hold and recheck every selected deployed source; never recreate"
      command models desc="prepare and prove required factory model capabilities; never activate"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --model-topology type=String arity=1..1 desc="explicit JSON model-serving topology; no secrets"
        option --approve type=String arity=1..1 desc="exact reviewed model capability fingerprint"
        option --verify-only type=Boolean arity=0..1 default=False choices=[False, True] desc="hold then prove current hosted model routes; no creation"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured result; never prompt"
      command source desc="advance bounded pinned acquisition; --prepare reviews its isolated host/store; never analyze"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --prepare type=Boolean arity=0..1 default=False choices=[False, True] desc="review/create only the source workload and source-only progress storage"
        option --runtime-image type=String arity=1..1 desc="implemented runtime image pinned by digest"
        option --source-audience type=String arity=1..1 desc="existing Entra API client ID; native operator/code consent required"
        option --approve type=String arity=1..1 desc="exact reviewed source preparation fingerprint"
        option --status-only type=Boolean arity=0..1 default=False choices=[False, True] desc="read trusted remote coverage without advancing"
        option --new-snapshot type=Boolean arity=0..1 default=False choices=[False, True] desc="explicitly replace current snapshot selection; retain bounded historical metadata"
        option --page type=Int32? arity=1..1 desc="zero-based coverage page"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret coverage; never prompt"
      command infer desc="infer grounded private charter draft; never publish or adopt"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --status-only type=Boolean arity=0..1 default=False choices=[False, True] desc="read private analysis coverage and draft without advancing"
        option --new-analysis type=Boolean arity=0..1 default=False choices=[False, True] desc="explicit new pinned analysis revision; never retry an unknown dispatch"
        option --page type=Int32? arity=1..1 desc="zero-based analysis evidence page"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret private draft review; never prompt"
      command charter desc="review, publish or adopt exact charter-only intent; never activate"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --publish type=Boolean arity=0..1 default=False choices=[False, True] desc="publish one explicitly approved charter-only PR using effective gh identity"
        option --adopt type=Boolean arity=0..1 default=False choices=[False, True] desc="review human-merged charter adoption; --approve records intent only"
        option --existing type=Boolean arity=0..1 default=False choices=[False, True] desc="explicit unchanged default-branch charter adoption without an empty PR"
        option --status-only type=Boolean arity=0..1 default=False choices=[False, True] desc="inspect original publication receipts without mutation"
        option --approve type=String arity=1..1 desc="exact current charter review fingerprint"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured private charter review; never prompt"
        option --inspect-publication type=Boolean arity=0..1 default=False choices=[False, True] desc="inspect historical publication without requiring current source/model readiness"
        option --reconcile type=Boolean arity=0..1 default=False choices=[False, True] desc="conditionally resolve original native response only; never send a GitHub mutation"
        option --retry type=Boolean arity=0..1 default=False choices=[False, True] desc="explicitly retry original known-no-effect request after fresh unchanged review"
        option --operation type=String arity=1..1 desc="original publication operation GUID for reconciliation or retry"
        option --revision type=String arity=1..1 desc="exact native publication revision from --inspect-publication"
      command publication desc="inspect or conditionally reconcile original proposal issue outcome; never adopt search matches"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --operation type=String arity=1..1 desc="original proposal operation ID from native decision output"
        option --revision type=String arity=1..1 desc="exact original native journal revision from inspection"
        option --attempt type=String arity=1..1 desc="original issued attempt GUID from inspection"
        option --reconcile type=Boolean arity=0..1 default=False choices=[False, True] desc="conditionally retain original dispatch-owned outcome; no GitHub mutation"
        option --retry type=Boolean arity=0..1 default=False choices=[False, True] desc="retry only original native known-no-effect outcome after all fresh live gates"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured redacted original outcome; never prompt"
      command preview desc="prepare, evaluate and inspect supervised council readiness; never activate or publish"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --preview-config type=String arity=1..1 desc="complete nonsecret preview preparation selection JSON"
        option --approve type=String arity=1..1 desc="exact reviewed preparation/publication fingerprint, or configured capability approval for evaluation"
        option --status type=Boolean arity=0..1 default=False choices=[False, True] desc="read retained preparation and current native receipts without writes"
        option --resume type=Boolean arity=0..1 default=False choices=[False, True] desc="continue the original compatible preparation or --evaluate operation; never replay uncertain effects"
        option --configure type=Boolean arity=0..1 default=False choices=[False, True] desc="review/approve protected capability publication, then the exact returned native execution seed"
        option --evaluate type=Boolean arity=0..1 default=False choices=[False, True] desc="execute the exact configured supervised evaluation; --resume retains the original operation"
        option --evaluation type=Boolean arity=0..1 default=False choices=[False, True] desc="inspect the original evaluation and current readiness without writes or inference"
        option --operation type=String arity=1..1 desc="exact configured preview operation ID; required for evaluation and inspection"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret preview plan, preparation or complete evaluation; never prompt"
      command audit desc="inspect, retain and export evidence; never authorize activation or infrastructure deletion"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --page type=Int32? arity=1..1 desc="zero-based retained evidence page"
        option --export type=String arity=1..1 desc="review or create an owner-only local export directory"
        option --verify-export type=String arity=1..1 desc="reverify protected receipt and current private artifact without writes"
        option --approve type=String arity=1..1 desc="exact current export, retention policy or expiry review fingerprint"
        option --approve-policy type=Boolean arity=0..1 default=False choices=[False, True] desc="review or approve the separate 90-day unused-analysis policy"
        option --expire-analysis type=String arity=1..1 desc="review or expire one exact eligible historical analysis revision"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured private audit evidence; never prompt"
      command pause desc="inspect or request durable council admission stop; never certify completed stop or activate"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --request type=Boolean arity=0..1 default=False choices=[False, True] desc="explicitly close admission without waiting for readiness or drain"
        option --operation type=String arity=1..1 desc="caller-chosen nonempty stop identifier; required with --request"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret stop state; never prompt"
      command activate desc="review operator approval or explicitly promote an exactly reviewed, currently ready native factory"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --operation type=String arity=1..1 desc="caller-chosen nonempty activation approval identifier"
        option --approve type=String arity=1..1 desc="exact review fingerprint; never inferred or a live-filing switch"
        option --promote type=Boolean arity=0..1 default=False choices=[False, True] desc="explicit final activation after separately reviewed native live and publication capabilities"
        option --status type=Boolean arity=0..1 default=False choices=[False, True] desc="read retained approval and current readiness without writes"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret approval state; never prompt"
      command live desc="review and prepare separately approved native Decide execution; never infer final activation"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted owner endpoint"
        option --product type=String arity=1..1 desc="reserved product"
        option --tenant type=String arity=1..1 desc="reserved tenant"
        option --subscription type=String arity=1..1 desc="reserved subscription"
        option --selection type=String arity=1..1 desc="complete native live rollout JSON"
        option --approve type=String arity=1..1 desc="exact live rollout fingerprint"
        option --status type=Boolean arity=0..1 default=False choices=[False, True] desc="verify original native rollout without writes"
        option --publication type=Boolean arity=0..1 default=False choices=[False, True] desc="separately review/provision the isolated native publication capability; never activate"
        option --exercise type=String arity=1..1 desc="explicit GUID for a separately scoped, nonpublishing native council exercise"
        option --execution type=String arity=1..1 desc="inspect original exercise GUID; never dispatch another evaluation"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="structured nonsecret native state; never prompt"
      command reserve desc="reserve durable factory identity and owner-scoped claims; never provision"
        option --input type=String arity=1..1 desc="complete reviewed version-1 selection JSON file"
        option --reviewed-fingerprint type=String arity=1..1 desc="explicit approval of the exact current full plan fingerprint"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="print nonsecret typed reservation/foundation state without prompts"
      command status desc="inspect trusted reservation and product foundation authority; read-only"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted prepared owner App Configuration endpoint"
        option --product type=String arity=1..1 desc="owner-scoped product key"
        option --tenant type=String arity=1..1 desc="explicit owner reservation tenant ID"
        option --subscription type=String arity=1..1 desc="explicit owner reservation subscription ID"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="print nonsecret typed reservation/foundation state without prompts"
      command resume desc="recover the original reservation/foundation at verified safe boundaries; uncertain effects stay held"
        option --owner-appconfig-endpoint type=String arity=1..1 desc="trusted prepared owner App Configuration endpoint"
        option --product type=String arity=1..1 desc="owner-scoped product key"
        option --tenant type=String arity=1..1 desc="explicit owner reservation tenant ID"
        option --subscription type=String arity=1..1 desc="explicit owner reservation subscription ID"
        option --json type=Boolean arity=0..1 default=False choices=[False, True] desc="print nonsecret typed reservation/foundation state without prompts"
        option --input type=String arity=1..1 desc="complete reviewed version-1 selection JSON file"
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
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="preview the owner bootstrap plan without side effects"
    option --yes type=Boolean arity=0..1 default=False choices=[False, True] desc="approve owner Azure and GitHub App creation without prompts"
    option --github-callback type=String arity=1..1 desc="GitHub App manifest callback URL or code for headless setup"
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
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="sweep the line but skip filing"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command serve-orchestrator desc="run the orchestrator worker (runtime)"
    option --loop type=Boolean arity=0..1 default=False choices=[False, True] desc="sweep continuously"
    option --interval type=Int32? arity=1..1 desc="seconds between sweeps"
    option --host type=String arity=1..1 default="0.0.0.0" desc="bind host"
    option --port type=Int32? arity=1..1 default=8080 desc="bind port"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command serve-agent desc="serve a source agent over A2A (runtime)"
    option --kind type=String arity=1..1 default="azuremonitor" desc="source agent kind"
    option --host type=String arity=1..1 default="0.0.0.0" desc="bind host"
    option --port type=Int32? arity=1..1 default=8080 desc="bind port"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command poll-outcomes desc="poll human outcome labels and record audited learning data (runtime)"
    option --dry-run type=Boolean arity=0..1 default=False choices=[False, True] desc="poll and preview outcomes without recording them"
    option --live type=Boolean arity=0..1 default=False choices=[False, True] desc="poll and record outcomes for real (requires the manual confirmation gate)"
    option --product type=String arity=1..1 desc="resolve runtime env for this product"
  command serve-github-auth desc="serve the isolated, inactive factory GitHub authentication module (runtime)"
  command serve-telemetry desc="serve isolated inactive Azure Monitor telemetry proof (runtime)"
  command serve-models desc="serve isolated inactive model capability workloads (runtime)"
  command serve-source desc="serve isolated inactive pinned source acquisition (runtime)"
  command serve-supplemental desc="serve isolated optional evidence proof (runtime)"
  command serve-preview desc="serve protected supervised council evaluation; never publish or activate (runtime)"
  command serve-decide desc="serve separately reviewed native Decide execution (runtime)"
  command serve-publisher desc="serve isolated native create-once publication (runtime)"
  command serve-publication-context desc="serve original council publication admission (runtime)"
  command serve-source-context desc="serve protected council-only adopted source context (runtime)"
  command serve-preview-models desc="serve protected council-only native model evaluation (runtime)"
  command serve-preview-telemetry desc="serve protected council-only application telemetry evidence (runtime)"
  command serve-preview-supplemental desc="serve protected council-only selected supplemental evidence (runtime)"
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
