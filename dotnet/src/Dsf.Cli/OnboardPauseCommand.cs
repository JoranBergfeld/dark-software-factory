using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildPauseCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("pause", "inspect or request durable council admission stop; never certify completed stop or activate");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var request = BoolOption("--request", "explicitly close admission without waiting for readiness or drain");
        var operation = StringOption("--operation", "caller-chosen nonempty stop identifier; required with --request");
        var json = BoolOption("--json", "structured nonsecret stop state; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, request, operation, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryPauseResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var name = Input(product);
                var tenantId = Identifier(Input(tenant)).ToString();
                var subscriptionId = Identifier(Input(subscription)).ToString();
                var requested = parse.GetValue(request);
                var stopId = parse.GetValue(operation);
                if (!requested && stopId is not null) throw new ArgumentException("--operation requires --request.");
                Guid? id = requested ? Identifier(Input(operation)) : null;
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var admission = await new FactoryReservationService(reviewer, runner, http, time)
                    .StatusAsync(owner, tenantId, subscriptionId, name, token);
                result = await new FactoryPauseService(runner, http, time).ExecuteAsync(admission, id, token);
            }
            catch (ArgumentException) { result = new(FactoryPauseState.Blocked, "Complete exact locators and --request with a nonempty --operation are required; no provider writes."); }
            catch (ReservationStoreException e)
            {
                result = new(e.State switch
                {
                    FactoryReservationState.Blocked => FactoryPauseState.Blocked,
                    FactoryReservationState.Failed => FactoryPauseState.Failed,
                    _ => FactoryPauseState.Unknown,
                }, e.Message + " Stop effect unconfirmed; inspect the original operation.");
            }
            catch (OperationCanceledException)
            { result = new(FactoryPauseState.Canceled, "Canceled; stop dispatch may already exist. Inspect original operation; cancellation is not rollback."); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException
                or HttpRequestException or IOException or OwnerReservationStore.AuthenticationFailedException)
            { result = new(FactoryPauseState.Unknown, "Protected stop or operator authority unconfirmed; provider details withheld. No admission or replacement authorized."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State is FactoryPauseState.NotRequested or FactoryPauseState.DrainingUnknown
                ? Success : result.State == FactoryPauseState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrEmpty(value) && interactive) value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrEmpty(value) ? value : throw new ArgumentException();
            }
            static Guid Identifier(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException();
        });
        return command;
    }
}
