using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildLiveCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("live", "review and prepare separately approved native Decide execution; never infer final activation");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var selection = StringOption("--selection", "complete native live rollout JSON");
        var approve = StringOption("--approve", "exact live rollout fingerprint");
        var status = BoolOption("--status", "verify original native rollout without writes");
        var exercise = StringOption("--exercise", "explicit GUID for a separately scoped, nonpublishing native council exercise");
        var execution = StringOption("--execution", "inspect original exercise GUID; never dispatch another evaluation");
        var json = BoolOption("--json", "structured nonsecret native state; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, selection, approve, status, exercise, execution, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryLiveResult result;
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var name = Input(product);
                var tenantId = Identifier(Input(tenant));
                var subscriptionId = Identifier(Input(subscription));
                var inspect = parse.GetValue(status);
                var invocation = parse.GetValue(exercise) ?? parse.GetValue(execution);
                if (parse.GetValue(exercise) is not null && parse.GetValue(execution) is not null
                    || invocation is not null && (inspect || parse.GetValue(selection) is not null)
                    || inspect && (parse.GetValue(selection) is not null || parse.GetValue(approve) is not null)) throw new ArgumentException();
                var request = invocation is null ? null : new FactoryLiveRequest(Guid.Parse(Identifier(invocation)), Input(approve));
                var selected = inspect || request is not null ? null : JsonSerializer.Deserialize<FactoryLiveSelection>(Input(selection), FactorySourceValidation.RecordJson)
                    ?? throw new JsonException();
                if (selected is not null) FactoryLive.Validate(selected);
                if (parse.GetValue(approve) is { } pin && !FactorySourceValidation.Hash(pin)) throw new ArgumentException();
                _ = new ProvisioningAssets().Resolve("onboard-live.json");
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var reviews = reviewer ?? RepositoryAttachmentReviewer.Create(runner);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewPreparation.Policy.RequestSeconds
                    + (request is null ? 0 : FactoryPreviewExecution.Policy.RequestSeconds)), time);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var transport = http ?? PreviewPreparationHttp;
                var admission = await new FactoryReservationService(reviews, runner, transport, time).StatusAsync(owner, tenantId, subscriptionId, name, pending.Token);
                var service = new FactoryPreviewPreparationService(reviews, runner, transport, time);
                if (request is not null)
                {
                    var observation = await service.ExerciseLiveAsync(admission, request, parse.GetValue(execution) is not null, pending.Token);
                    terminal.WriteLine(JsonSerializer.Serialize(observation, ReviewJson));
                    return observation.Readiness == PreviewReadiness.ReadyAwaitingActivation ? Success : Failure;
                }
                result = await service.LiveAsync(admission, selected, parse.GetValue(approve), pending.Token);
            }
            catch (Exception e) when (e is JsonException or ArgumentException or FormatException)
            { result = new(FactoryLiveState.Blocked, "Complete explicit locators and native selection required; status is mutually exclusive. No provider writes."); }
            catch (OperationCanceledException) { result = new(FactoryLiveState.Canceled, "Canceled; inspect original operation, not rollback."); }
            catch (Exception e) when (e is ReservationStoreException or IOException or InvalidOperationException or HttpRequestException
                or OwnerReservationStore.AuthenticationFailedException)
            { result = new(FactoryLiveState.Unknown, "Native rollout authority or asset unavailable; no fallback."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State is FactoryLiveState.Reviewed or FactoryLiveState.PreparedAwaitingPublication ? Success
                : result.State == FactoryLiveState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrEmpty(value) && terminal.Capabilities.IsInteractive && !parse.GetValue(json))
                    value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrEmpty(value) ? value : throw new ArgumentException();
            }
            static string Identifier(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString() : throw new ArgumentException();
        });
        return command;
    }
}
