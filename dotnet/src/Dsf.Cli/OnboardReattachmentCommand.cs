using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildOnboardingReattachCommand(
        ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("reattach",
            "review or execute explicit same-identity recovery of a detached, non-purged council");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "retained product");
        var tenant = StringOption("--tenant", "retained tenant");
        var subscription = StringOption("--subscription", "retained subscription");
        var operation = StringOption("--operation", "caller-chosen nonempty reattachment operation identifier");
        var approve = StringOption("--approve", "exact current reattachment-plan fingerprint");
        var json = BoolOption("--json", "structured nonsecret plan/state; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, operation, approve, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryReattachmentResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var name = Input(product);
                var tenantId = Identifier(Input(tenant)).ToString();
                var subscriptionId = Identifier(Input(subscription)).ToString();
                var id = Identifier(Input(operation));
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var admission = await new FactoryReservationService(reviewer, runner, http, time)
                    .StatusAsync(owner, tenantId, subscriptionId, name, token);
                result = await new FactoryReattachmentService(reviewer, runner, http, time)
                    .ExecuteAsync(admission, id, parse.GetValue(approve)?.Trim(), token);
            }
            catch (ArgumentException)
            {
                result = new(FactoryReattachmentState.Blocked,
                    "Complete exact owner/product/tenant/subscription locators and nonempty --operation required; no provider writes.");
            }
            catch (ReservationStoreException e)
            {
                result = new(e.State switch
                {
                    FactoryReservationState.Blocked => FactoryReattachmentState.Blocked,
                    FactoryReservationState.Failed => FactoryReattachmentState.Failed,
                    _ => FactoryReattachmentState.Unknown,
                }, e.Message);
            }
            catch (OperationCanceledException)
            {
                result = new(FactoryReattachmentState.Canceled,
                    "Canceled; remote effects may already exist. Inspect original operation; cancellation is not rollback.");
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException
                or KeyNotFoundException or HttpRequestException or IOException
                or OwnerReservationStore.AuthenticationFailedException)
            {
                result = new(FactoryReattachmentState.Unknown,
                    "Trusted retained identity, drift or conditional recovery outcome unconfirmed; no forced retry.");
            }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReattachmentState.AwaitingReadiness ? Success
                : result.State == FactoryReattachmentState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrEmpty(value) && interactive)
                    value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrEmpty(value) ? value : throw new ArgumentException();
            }
            static Guid Identifier(string value) =>
                Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException();
        });
        return command;
    }
}
