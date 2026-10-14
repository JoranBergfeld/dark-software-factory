using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildOnboardingOffboardCommand(
        ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("offboard", "review or execute council-only detach; retain application, repository, identity, audit and disabled infrastructure");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var operation = StringOption("--operation", "caller-chosen nonempty detach operation identifier");
        var approve = StringOption("--approve", "exact current detach-plan fingerprint");
        var json = BoolOption("--json", "structured nonsecret detach plan/state; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, operation, approve, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryOffboardingResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var name = Input(product);
                var tenantId = Identifier(Input(tenant)).ToString();
                var subscriptionId = Identifier(Input(subscription)).ToString();
                var id = Identifier(Input(operation));
                var approval = parse.GetValue(approve)?.Trim();
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var admission = await new FactoryReservationService(reviewer, runner, http, time)
                    .StatusAsync(owner, tenantId, subscriptionId, name, token);
                result = await new FactoryOffboardingService(runner, http, time)
                    .ExecuteAsync(admission, id, approval, token);
            }
            catch (ArgumentException)
            {
                result = new(FactoryOffboardingState.Blocked,
                    "Complete exact owner/product/tenant/subscription locators and nonempty --operation required; no provider writes.");
            }
            catch (ReservationStoreException e)
            {
                result = new(e.State switch
                {
                    FactoryReservationState.Blocked => FactoryOffboardingState.Blocked,
                    FactoryReservationState.Failed => FactoryOffboardingState.Failed,
                    _ => FactoryOffboardingState.Unknown,
                }, e.Message + " Detach outcome unconfirmed; inspect the original operation.");
            }
            catch (OperationCanceledException)
            {
                result = new(FactoryOffboardingState.Canceled,
                    "Canceled; admission-close or cleanup effects may already exist. Inspect original operation; cancellation is not rollback.");
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException
                or KeyNotFoundException or HttpRequestException or IOException
                or OwnerReservationStore.AuthenticationFailedException)
            {
                result = new(FactoryOffboardingState.Unknown,
                    "Trusted ownership, quiescence or cleanup outcome unconfirmed; provider details withheld. No forced retry or completed-stop claim.");
            }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryOffboardingState.Detached ? Success
                : result.State == FactoryOffboardingState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrEmpty(value) && interactive) value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrEmpty(value) ? value : throw new ArgumentException();
            }
            static Guid Identifier(string value) =>
                Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException();
        });
        return command;
    }
}
