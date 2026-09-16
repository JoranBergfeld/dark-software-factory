using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildIntentCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("infer", "infer grounded private charter draft; never publish or adopt");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var status = BoolOption("--status-only", "read private analysis coverage and draft without advancing");
        var newAnalysis = BoolOption("--new-analysis", "explicit new pinned analysis revision; never retry an unknown dispatch");
        var page = IntOption("--page", "zero-based analysis evidence page");
        var json = BoolOption("--json", "structured nonsecret private draft review; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, status, newAnalysis, page, json);
        command.SetAction(async (parse, token) =>
        {
            IntentResult output;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                if (parse.GetValue(status) && parse.GetValue(newAnalysis))
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "Status cannot select a new analysis.");
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var productKey = Input(product);
                var tenantId = Guid.Parse(Input(tenant)).ToString();
                var subscriptionId = Guid.Parse(Input(subscription)).ToString();
                var request = new IntentRequest(parse.GetValue(newAnalysis), parse.GetValue(page) ?? 0);
                if (request.Page is < 0 or >= FactoryIntentLimits.MaximumPage)
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "Invalid analysis page; no effects.");
                if (interactive) terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, ReviewText(
                    "dsf onboard decide infer --owner-appconfig-endpoint " + owner + " --product " + productKey
                    + " --tenant " + tenantId + " --subscription " + subscriptionId
                    + (parse.GetValue(status) ? " --status-only" : "") + (request.NewAnalysis ? " --new-analysis" : "")
                    + " --page " + request.Page)));
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var reservations = new FactoryReservationService(reviewer, runner, http, time);
                var admission = await reservations.StatusAsync(owner, tenantId, subscriptionId, productKey, token);
                output = await new FactorySourceService(runner, http, time).InferAsync(admission, request, parse.GetValue(status), token);
                var current = await reservations.StatusAsync(owner, tenantId, subscriptionId, productKey, token);
                if (current.State != FactoryReservationState.Reserved || current.Identity != admission.Identity
                    || current.Foundation?.State != FactoryFoundationState.Inactive)
                    output = FailureResult(IntentState.Unknown, "Final owner-registry authority unconfirmed; draft withheld. No publication or authority revocation asserted.");
            }
            catch (ReservationStoreException e)
            { output = FailureResult(IntentState.Blocked, e.Message); }
            catch (OperationCanceledException)
            { output = FailureResult(IntentState.Canceled, "Canceled; remote model dispatch/progress may remain Unknown. Inspect private status; no publication or forced retry."); }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException
                or HttpRequestException or FormatException or OwnerReservationStore.AuthenticationFailedException)
            { output = FailureResult(IntentState.Blocked, "Current approved source/model authority required; no draft released, fallback or publication. Provider details withheld."); }
            terminal.WriteLine(JsonSerializer.Serialize(output, ReviewJson));
            return output.State == IntentState.DraftReady ? Success : output.State == IntentState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrWhiteSpace(value) && interactive) value = terminal.Prompt(option.Name + " (" + option.Description + "): ")?.Trim();
                return !string.IsNullOrWhiteSpace(value) ? value
                    : throw new ReservationStoreException(FactoryReservationState.Blocked, option.Name + " is required; no provider access.");
            }
        });
        return command;
    }

    private static IntentResult FailureResult(IntentState state, string detail) => new(state, detail, null, null, null, [], [], [], null);
}
