using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildGitHubAuthorityCommand(
        ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer, IAzureCliRunner? azure,
        HttpClient? http, TimeProvider? clock)
    {
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner reservation endpoint");
        var product = StringOption("--product", "reserved product key");
        var tenant = StringOption("--tenant", "reserved tenant ID");
        var subscription = StringOption("--subscription", "reserved subscription ID");
        var json = BoolOption("--json", "structured nonsecret authority result; never prompt");
        var source = StringOption("--source-vault-id", "exact existing owner vault ARM ID, including its subscription");
        var version = StringOption("--source-key-version", "approved existing owner signing key version");
        var image = StringOption("--runtime-image", "implemented DSF runtime image pinned by sha256 digest");
        var audience = StringOption("--auth-audience", "existing single-tenant API application client ID for workload authentication");
        var approval = StringOption("--approve", "exact authority preview fingerprint; accepts disclosed broader signing risk");
        var verifyOnly = BoolOption("--verify-only", "refresh deployed proof only; never retry key copies, grants or deployments");
        var command = new Command("github", "prepare and verify only the approved factory GitHub authority");
        AddOptions(command, endpoint, product, tenant, subscription, json, source, version, image, audience, approval, verifyOnly);
        command.SetAction(async (parse, token) =>
        {
            FactoryReservationResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            var prompted = false;
            try
            {
                if (parse.GetValue(verifyOnly) && new[] { approval, source, version, image, audience }.Any(o => parse.GetValue(o) is not null))
                    throw new BoundaryInputException("--verify-only uses existing trusted authority; do not supply new preparation inputs or --approve.");
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint) ?? "");
                var key = Input(product) ?? throw new JsonException();
                var tenantId = Guid.Parse(Input(tenant) ?? "").ToString();
                var subscriptionId = Guid.Parse(Input(subscription) ?? "").ToString();
                foreach (var asset in parse.GetValue(verifyOnly) ? Array.Empty<string>() : new[] { "product-github-private-key.json", "onboard-github-auth.json" })
                {
                    try { _ = new ProvisioningAssets().Resolve(asset); }
                    catch (InvalidOperationException)
                    { throw new BoundaryInputException("Packaged " + asset + " is missing, unreadable or invalid; reinstall the CLI. No provider access attempted."); }
                }
                result = await new FactoryReservationService(reviewer, azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System)
                    .StatusAsync(owner, tenantId, subscriptionId, key, token);
                if (parse.GetValue(verifyOnly))
                    result = await new FactoryGitHubAuthorityService(azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System, reviewer)
                        .ReverifyAsync(result, token);
                else if (result.GitHubAuthority is null)
                {
                    var sourceId = Input(source);
                    var sourceVersion = Input(version);
                    var runtimeImage = Input(image);
                    var audienceId = Input(audience);
                    var authority = FactoryGitHubAuthorityService.Preview(result, sourceId, sourceVersion, runtimeImage, audienceId);
                    var approved = parse.GetValue(approval);
                    if (string.IsNullOrWhiteSpace(approved) && interactive)
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(authority, ReviewJson));
                        prompted = true;
                        approved = terminal.Prompt("Accept the displayed exact copy, grants, host and broader signing risk: enter approvalFingerprint (blank = preview only): ")?.Trim();
                    }
                    if (prompted)
                    {
                        string[] arguments = ["dsf", "onboard", "decide", "github", "--owner-appconfig-endpoint", owner,
                            "--product", key, "--tenant", tenantId, "--subscription", subscriptionId,
                            "--source-vault-id", sourceId!, "--source-key-version", sourceVersion!,
                            "--runtime-image", runtimeImage!, "--auth-audience", audienceId!,
                            .. string.IsNullOrWhiteSpace(approved) ? Array.Empty<string>() : new[] { "--approve", approved }];
                        terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities,
                            ReviewText(string.Join(' ', arguments.Select(argument => Regex.IsMatch(argument, @"^[-A-Za-z0-9_./:@]+$")
                                ? argument : "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                    }
                    result = !string.IsNullOrWhiteSpace(approved)
                        ? await new FactoryGitHubAuthorityService(azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System, reviewer)
                            .ApplyAsync(result, authority, approved, token)
                        : result with { State = FactoryReservationState.Partial,
                            Detail = "Read-only authority preview; separate exact --approve required. " + FactoryGitHubAuthorityService.Risk,
                            GitHubAuthority = authority };
                }
                else if (result.GitHubAuthority is { } existing
                    && (Differs(source, existing.SourceVaultId, StringComparison.OrdinalIgnoreCase)
                        || Differs(version, existing.SourceSecretVersion, StringComparison.Ordinal)
                        || Differs(image, existing.RuntimeImage, StringComparison.Ordinal)
                        || Differs(audience, existing.AudienceClientId, StringComparison.OrdinalIgnoreCase)
                        || Differs(approval, existing.ApprovalFingerprint, StringComparison.Ordinal)))
                    result = result with { State = FactoryReservationState.Blocked,
                        Detail = "Preparation inputs differ from existing authority; no new approval, replacement or retry. Inspect the held operation or use --verify-only without new inputs." };
            }
            catch (BoundaryInputException e) { result = new(FactoryReservationState.Failed, e.Message); }
            catch (ReservationStoreException e) { result = new(e.State, e.Message); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { result = new(FactoryReservationState.Canceled, "Canceled; no new GitHub authority effects."); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
            { result = new(FactoryReservationState.Failed, "Exact trusted owner/product/tenant/subscription required; details withheld."); }
            if (!parse.GetValue(json)) terminal.WriteLine(ReviewText($"[dsf] {result.State}: {result.Detail}"));
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReservationState.Reserved && result.GitHubAuthority?.State == GitHubAuthorityState.Verified ? Success
                : result.State == FactoryReservationState.Canceled ? CanceledExitCode : Failure;

            string? Input(Option<string> option)
            {
                var value = parse.GetValue(option);
                if (string.IsNullOrWhiteSpace(value) && interactive)
                {
                    prompted = true;
                    value = terminal.Prompt(option.Name + " (" + option.Description + "): ");
                }
                return value?.Trim();
            }

            bool Differs(Option<string> option, string? expected, StringComparison comparison) =>
                parse.GetValue(option) is { } supplied && !string.Equals(supplied.Trim(), expected, comparison);
        });
        return command;
    }
}
