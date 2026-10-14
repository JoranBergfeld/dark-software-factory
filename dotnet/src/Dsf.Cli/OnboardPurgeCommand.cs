using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildOnboardingPurgeCommand(
        ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("purge", "review or remove exact owned retained factory infrastructure and permanently retire its identity");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "detached product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var operation = StringOption("--operation", "caller-chosen nonempty purge operation identifier");
        var approve = StringOption("--approve", "exact current purge-plan fingerprint");
        var auditExport = StringOption("--audit-export", "absolute private export path verified against current audit roots");
        var json = BoolOption("--json", "structured nonsecret purge plan/state; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, operation, approve, auditExport, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryPurgeResult result;
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
                result = await new FactoryPurgeService(runner, http, time)
                    .ExecuteAsync(admission, id, parse.GetValue(approve)?.Trim(),
                        parse.GetValue(auditExport)?.Trim(), token);
            }
            catch (ArgumentException)
            {
                result = new(FactoryPurgeState.Blocked,
                    "Complete exact owner/product/tenant/subscription locators and nonempty --operation required; no provider writes.");
            }
            catch (OperationCanceledException)
            {
                result = new(FactoryPurgeState.Canceled,
                    "Canceled; remote deletion may already exist. Inspect original operation; cancellation is not rollback.");
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException
                or KeyNotFoundException or HttpRequestException or IOException or ReservationStoreException
                or OwnerReservationStore.AuthenticationFailedException)
            {
                result = new(FactoryPurgeState.Unknown,
                    "Current ownership, audit disposition or deletion outcome unconfirmed (" + e.GetType().Name
                    + "); provider details withheld. No forced retry or success claim.");
            }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryPurgeState.Purged ? Success
                : result.State == FactoryPurgeState.Canceled ? CanceledExitCode : Failure;

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
