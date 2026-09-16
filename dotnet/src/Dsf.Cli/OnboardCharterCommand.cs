using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildOnboardCharterCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("charter", "review, publish or adopt exact charter-only intent; never activate");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var publish = BoolOption("--publish", "publish one explicitly approved charter-only PR using effective gh identity");
        var adopt = BoolOption("--adopt", "review human-merged charter adoption; --approve records intent only");
        var existing = BoolOption("--existing", "explicit unchanged default-branch charter adoption without an empty PR");
        var status = BoolOption("--status-only", "inspect original publication receipts without mutation");
        var approve = StringOption("--approve", "exact current charter review fingerprint");
        var json = BoolOption("--json", "structured private charter review; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, publish, adopt, existing, status, approve, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryCharterResult output;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                if (parse.GetValue(publish) && (parse.GetValue(adopt) || parse.GetValue(existing))
                    || parse.GetValue(existing) && !parse.GetValue(adopt)
                    || parse.GetValue(approve) is not null && !parse.GetValue(adopt) && !parse.GetValue(publish)
                    || parse.GetValue(status) && (parse.GetValue(publish) || parse.GetValue(adopt) || parse.GetValue(approve) is not null))
                    throw new CharterOperationException(FactoryCharterState.Blocked, "Status is read-only; mutation options cannot be combined.");
                if (parse.GetValue(publish) && !interactive && string.IsNullOrWhiteSpace(parse.GetValue(approve)))
                    throw new CharterOperationException(FactoryCharterState.Blocked, "--approve current exact fingerprint required; no provider access.");
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var key = Input(product);
                var tenantId = Guid.Parse(Input(tenant)).ToString();
                var subscriptionId = Guid.Parse(Input(subscription)).ToString();
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var reviews = reviewer ?? RepositoryAttachmentReviewer.Create(runner);
                var reservations = new FactoryReservationService(reviews, runner, http, time);
                var admission = await reservations.StatusAsync(owner, tenantId, subscriptionId, key, token);
                if (admission.State != FactoryReservationState.Reserved || admission.Foundation?.State != FactoryFoundationState.Inactive)
                    throw new CharterOperationException(FactoryCharterState.Blocked, "Current owner registration and inactive factory required.");
                var service = new FactoryCharterService(reviews, runner, http, time);
                output = parse.GetValue(status) ? await service.StatusAsync(admission, token)
                    : parse.GetValue(adopt) ? await service.ReviewAdoptionAsync(admission, parse.GetValue(existing), token)
                    : await service.PreviewAsync(admission, false, token);
                if (parse.GetValue(publish))
                {
                    var approval = parse.GetValue(approve);
                    if (interactive && string.IsNullOrWhiteSpace(approval))
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(output, ReviewJson));
                        approval = terminal.Prompt("Approve this exact charter-only PR by entering its revision (blank cancels): ");
                    }
                    PrintReplay(approval);
                    output = await service.PublishAsync(admission, output, approval ?? "", token);
                }
                if (parse.GetValue(adopt) && (parse.GetValue(approve) is not null || interactive))
                {
                    var approval = parse.GetValue(approve);
                    if (interactive && string.IsNullOrWhiteSpace(approval))
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(output, ReviewJson));
                        approval = terminal.Prompt("Approve exact charter adoption by entering its revision (blank cancels): ");
                    }
                    PrintReplay(approval);
                    output = await service.AdoptAsync(admission, output, parse.GetValue(existing), approval ?? "", token);
                }
                if (interactive && !parse.GetValue(publish) && !parse.GetValue(adopt))
                    PrintReplay(null);
                var current = await reservations.StatusAsync(owner, tenantId, subscriptionId, key, token);
                if (current.State != FactoryReservationState.Reserved || current.Identity != admission.Identity
                    || current.Foundation?.State != FactoryFoundationState.Inactive)
                    throw new CharterOperationException(FactoryCharterState.Unknown, "Final owner authority unconfirmed; review withheld.");

                void PrintReplay(string? approval)
                {
                    if (!interactive) return;
                    if (approval is not null && approval != output.Revision)
                        throw new CharterOperationException(string.IsNullOrWhiteSpace(approval) ? FactoryCharterState.Canceled : FactoryCharterState.Blocked,
                            "Exact approval not given; no mutation dispatched.");
                    var args = new List<string> { "dsf", "onboard", "decide", "charter" };
                    if (parse.GetValue(publish)) args.Add("--publish");
                    if (parse.GetValue(adopt)) args.Add("--adopt");
                    if (parse.GetValue(existing)) args.Add("--existing");
                    if (parse.GetValue(status)) args.Add("--status-only");
                    args.AddRange(["--owner-appconfig-endpoint", owner, "--product", key, "--tenant", tenantId, "--subscription", subscriptionId]);
                    if (approval is not null) args.AddRange(["--approve", approval]);
                    terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, ReviewText(string.Join(' ', args.Select(a =>
                        System.Text.RegularExpressions.Regex.IsMatch(a, @"^[-A-Za-z0-9_./:]+$") ? a : "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                }
            }
            catch (CharterOperationException e) { output = new(e.State, e.Message); }
            catch (ReservationStoreException e) { output = new(Enum.Parse<FactoryCharterState>(e.State.ToString()), e.Message); }
            catch (OperationCanceledException) { output = new(FactoryCharterState.Canceled, "Canceled; no rollback asserted. Inspect durable publication status."); }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException
                or HttpRequestException or FormatException or CharterParseException or OwnerReservationStore.AuthenticationFailedException)
            { output = new(FactoryCharterState.Unknown, "Current charter/source/model/operator authority unconfirmed; provider details withheld. No forced retry."); }
            if (output.State is not (FactoryCharterState.Reviewed or FactoryCharterState.Published or FactoryCharterState.Adopted))
                output = output with { BlockedOperation = parse.GetValue(publish) ? "Publication" : parse.GetValue(adopt) ? "Adoption" : "CharterReview",
                    NextAction = "Inspect current authority and original durable receipts. Restore access or review complete changed source; never force retry an uncertain effect." };
            terminal.WriteLine(JsonSerializer.Serialize(output, ReviewJson));
            return output.State is FactoryCharterState.Reviewed or FactoryCharterState.Published or FactoryCharterState.Adopted ? Success
                : output.State == FactoryCharterState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrWhiteSpace(value) && interactive) value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrWhiteSpace(value) ? value
                    : throw new CharterOperationException(FactoryCharterState.Blocked, option.Name + " required; no provider access.");
            }
        });
        return command;
    }
}
