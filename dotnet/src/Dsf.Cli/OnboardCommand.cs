using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static readonly JsonSerializerOptions ReviewJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Command BuildOnboardCommand(
        ICliTerminal terminal, IReadOnlySet<string> providedOptions, RepositoryAttachmentReviewer? reviewer, IAzureCliRunner? boundaryAzure,
        HttpClient? reservationHttp, TimeProvider? reservationClock, HttpMessageHandler? modelProofTransport)
    {
        var repository = StringOption("--repo", "existing GitHub.com repository (OWNER/REPO)");
        var ownerAppConfig = StringOption("--owner-appconfig-endpoint", "prepared owner App Configuration endpoint (or DSF_OWNER_APPCONFIG_ENDPOINT)");
        var ownerVault = StringOption("--owner-keyvault-uri", "prepared owner Key Vault URI (or DSF_OWNER_KEYVAULT_URI)");
        var issueAutomation = StringOption("--issue-automation", "external-owner declaration for proposal issues", "unknown", "unknown", "clear", "conflict");
        var prAutomation = StringOption("--pr-automation", "external-owner declaration for charter PRs", "unknown", "unknown", "clear", "conflict");
        var labelAutomation = StringOption("--label-automation", "external-owner declaration for label creation", "unknown", "unknown", "clear", "conflict");
        var automationOwner = StringOption("--automation-owner", "accountable external automation owner/team; required for clear/conflict declarations");
        var json = BoolOption("--json", "print typed, nonsecret repository-review facts as JSON");
        var review = new Command("review", "read-only repository attachment assessment; does not onboard or activate");
        AddOptions(review, repository, ownerAppConfig, ownerVault, issueAutomation, prAutomation, labelAutomation, automationOwner, json);
        review.SetAction(async (parse, cancellationToken) =>
        {
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            var prompted = false;
            var selected = parse.GetValue(repository);
            if (string.IsNullOrWhiteSpace(selected) && interactive)
            {
                prompted = true;
                selected = terminal.Prompt("Existing GitHub repository (OWNER/REPO): ");
            }

            if (string.IsNullOrWhiteSpace(selected))
            {
                throw new InvalidOperationException(
                    "--repo is required when prompts are unavailable. Run: dsf onboard decide review --repo OWNER/REPO");
            }

            if (!Regex.IsMatch(selected, @"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$")
                || selected.Split('/')[1] is "." or "..")
            {
                throw new InvalidOperationException("--repo must be an explicit GitHub.com OWNER/REPO, not a URL or product key.");
            }

            var appConfigEndpoint = Input(ownerAppConfig, "DSF_OWNER_APPCONFIG_ENDPOINT", "Owner App Configuration endpoint (blank = unknown): ");
            var keyVaultUri = Input(ownerVault, "DSF_OWNER_KEYVAULT_URI", "Owner Key Vault URI (blank = unknown): ");
            var declarations = new Dictionary<RepositoryMutationClass, RepositoryAutomationDeclaration>
            {
                [RepositoryMutationClass.ProposalIssue] = Declaration(issueAutomation),
                [RepositoryMutationClass.CharterPullRequest] = Declaration(prAutomation),
                [RepositoryMutationClass.LabelCreation] = Declaration(labelAutomation),
            };
            var declaredOwner = parse.GetValue(automationOwner);
            if (declarations.Values.Any(value => value != RepositoryAutomationDeclaration.Unknown)
                && string.IsNullOrWhiteSpace(declaredOwner) && interactive)
            {
                prompted = true;
                declaredOwner = terminal.Prompt("Accountable external automation owner/team: ")?.Trim();
            }

            if (declarations.Values.Any(value => value != RepositoryAutomationDeclaration.Unknown)
                && string.IsNullOrWhiteSpace(declaredOwner))
            {
                throw new InvalidOperationException("--automation-owner is required for clear/conflict declarations; declarations are not technical proof.");
            }

            var assessment = await (reviewer ?? RepositoryAttachmentReviewer.Create()).ReviewAsync(
                new RepositoryAttachmentReviewRequest(selected, appConfigEndpoint, keyVaultUri)
                {
                    AutomationDeclarations = declarations,
                    AutomationOwner = declaredOwner,
                }, cancellationToken);
            if (parse.GetValue(json))
            {
                terminal.WriteLine(JsonSerializer.Serialize(assessment, ReviewJson));
            }
            else
            {
                if (prompted)
                {
                    var arguments = new List<string> { "dsf", "onboard", "decide", "review", "--repo", selected };
                    AddValue(arguments, "--owner-appconfig-endpoint", appConfigEndpoint);
                    AddValue(arguments, "--owner-keyvault-uri", keyVaultUri);
                    AddValue(arguments, "--issue-automation", declarations[RepositoryMutationClass.ProposalIssue].ToString().ToLowerInvariant());
                    AddValue(arguments, "--pr-automation", declarations[RepositoryMutationClass.CharterPullRequest].ToString().ToLowerInvariant());
                    AddValue(arguments, "--label-automation", declarations[RepositoryMutationClass.LabelCreation].ToString().ToLowerInvariant());
                    AddValue(arguments, "--automation-owner", declaredOwner);
                    terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities,
                        ReviewText(string.Join(' ', arguments.Select(argument => Regex.IsMatch(argument, @"^[-A-Za-z0-9_./:]+$")
                            ? argument : "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                }

                terminal.WriteLine($"[dsf] Repository attachment review: {assessment.State}. Not onboarded; no remote changes.");
                if (assessment.Repository is { } observed)
                {
                    terminal.WriteLine(ReviewText($"[dsf] Repository {observed.Id}: {observed.Owner}/{observed.Name}; default branch: {observed.DefaultBranch}"));
                }

                foreach (var finding in assessment.Findings)
                {
                    terminal.WriteLine(ReviewText($"[dsf] {finding.Check}: {finding.State}. {finding.Detail}"
                        + (finding.NextActor is null ? "" : $" Next actor: {finding.NextActor}. {finding.NextAction}")));
                }
            }

            return assessment.State is RepositoryReviewState.Confirmed ? Success : Failure;

            string? Input(Option<string> option, string environment, string prompt)
            {
                var value = parse.GetValue(option) ?? Environment.GetEnvironmentVariable(environment);
                if (string.IsNullOrWhiteSpace(value) && interactive && !providedOptions.Contains(option.Name))
                {
                    prompted = true;
                    value = terminal.Prompt(prompt);
                }

                return value?.Trim();
            }

            RepositoryAutomationDeclaration Declaration(Option<string> option)
            {
                var value = parse.GetValue(option);
                if (interactive && !providedOptions.Contains(option.Name))
                {
                    prompted = true;
                    value = terminal.Prompt($"{option.Name} (unknown/clear/conflict; blank = unknown): ");
                }

                return value?.Trim().ToLowerInvariant() switch
                {
                    null or "" or "unknown" => RepositoryAutomationDeclaration.Unknown,
                    "clear" => RepositoryAutomationDeclaration.Clear,
                    "conflict" => RepositoryAutomationDeclaration.Conflict,
                    _ => throw new InvalidOperationException($"{option.Name} must be unknown, clear or conflict."),
                };
            }
        });
        var decide = new Command("decide", "assess attachment of an existing application to Decide");
        decide.Subcommands.Add(review);
        decide.Subcommands.Add(BuildBoundaryPlanCommand(terminal, reviewer, boundaryAzure));
        decide.Subcommands.Add(BuildFoundationCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildGitHubAuthorityCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildTelemetryCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildSupplementalCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildModelsCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock, modelProofTransport));
        decide.Subcommands.Add(BuildSourceCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildIntentCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildOnboardCharterCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildPublicationCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildPreviewCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildAuditCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildPauseCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildOnboardingOffboardCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildReconfigureCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildActivationCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildLiveCommand(terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildReservationCommand("reserve", terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildReservationCommand("status", terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        decide.Subcommands.Add(BuildReservationCommand("resume", terminal, reviewer, boundaryAzure, reservationHttp, reservationClock));
        var onboard = new Command("onboard", "stage existing application attachment and inactive foundation without greenfield provisioning");
        onboard.Subcommands.Add(decide);
        return onboard;
    }

    private static string ReviewText(string value) => string.Concat(value.Select(character =>
        char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
            ? $"\\u{(int)character:x4}" : character.ToString()));
}
