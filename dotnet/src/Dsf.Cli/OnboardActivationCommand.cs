using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildActivationCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("activate", "review or retain exact operator activation approval; inactive runtime promotion remains blocked");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var operation = StringOption("--operation", "caller-chosen nonempty activation approval identifier");
        var approve = StringOption("--approve", "exact review fingerprint; never inferred or a live-filing switch");
        var status = BoolOption("--status", "read retained approval and current readiness without writes");
        var json = BoolOption("--json", "structured nonsecret approval state; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, operation, approve, status, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryActivationResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var name = Input(product);
                var tenantId = Identifier(Input(tenant)).ToString();
                var subscriptionId = Identifier(Input(subscription)).ToString();
                var inspect = parse.GetValue(status);
                var approval = parse.GetValue(approve);
                if (inspect && (approval is not null || parse.GetValue(operation) is not null)
                    || approval is not null && !FactorySourceValidation.Hash(approval)) throw new ArgumentException();
                Guid? id = inspect ? null : Identifier(Input(operation));
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var reviews = reviewer ?? RepositoryAttachmentReviewer.Create(runner);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewExecution.Policy.RequestSeconds), time);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var transport = http ?? PreviewPreparationHttp;
                var admission = await new FactoryReservationService(reviews, runner, transport, time)
                    .StatusAsync(owner, tenantId, subscriptionId, name, pending.Token);
                result = await new FactoryActivationService(reviews, runner, transport, time)
                    .ExecuteAsync(admission, id, approval, pending.Token);
            }
            catch (ArgumentException) { result = new(FactoryActivationState.Blocked, "Exact locators and --operation required; --status is read-only and mutually exclusive with review/approval. No provider writes."); }
            catch (OperationCanceledException) { result = new(FactoryActivationState.Canceled, "Canceled before approval admission; inspect original operation."); }
            catch (Exception e) when (e is ReservationStoreException or JsonException or FormatException or InvalidOperationException
                or KeyNotFoundException or HttpRequestException or IOException or OwnerReservationStore.AuthenticationFailedException)
            { result = new(FactoryActivationState.Unknown, "Protected owner/operator authority unavailable; no activation or approval fallback."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryActivationState.Reviewed || parse.GetValue(status) && result.State == FactoryActivationState.ApprovalOnly
                ? Success : result.State == FactoryActivationState.Canceled ? CanceledExitCode : Failure;

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
