using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static readonly HttpClient PreviewPreparationHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    private static Command BuildPreviewCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("preview", "prepare, evaluate and inspect supervised council readiness; never activate or publish");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var configuration = StringOption("--preview-config", "complete nonsecret preview preparation selection JSON");
        var approval = StringOption("--approve", "exact reviewed preparation/publication fingerprint, or configured capability approval for evaluation");
        var status = BoolOption("--status", "read retained preparation and current native receipts without writes");
        var resume = BoolOption("--resume", "continue the original compatible preparation or --evaluate operation; never replay uncertain effects");
        var configure = BoolOption("--configure", "review/approve protected capability publication, then the exact returned native execution seed");
        var evaluate = BoolOption("--evaluate", "execute the exact configured supervised evaluation; --resume retains the original operation");
        var evaluation = BoolOption("--evaluation", "inspect the original evaluation and current readiness without writes or inference");
        var operation = StringOption("--operation", "exact configured preview operation ID; required for evaluation and inspection");
        var json = BoolOption("--json", "structured nonsecret preview plan, preparation or complete evaluation; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, configuration, approval, status, resume, configure, evaluate, evaluation, operation, json);
        command.SetAction(async (parse, token) =>
        {
            if (parse.GetValue(evaluate) || parse.GetValue(evaluation) || parse.GetValue(operation) is not null)
                return await InvokePreviewEvaluationAsync(terminal, reviewer, azure, http, clock,
                    parse.GetValue(endpoint), parse.GetValue(product), parse.GetValue(tenant), parse.GetValue(subscription),
                    parse.GetValue(operation), parse.GetValue(approval), parse.GetValue(evaluation),
                    parse.GetValue(evaluate) == parse.GetValue(evaluation) || parse.GetValue(configure) || parse.GetValue(status)
                        || parse.GetValue(configuration) is not null || parse.GetValue(resume) && !parse.GetValue(evaluate),
                    terminal.Capabilities.IsInteractive && !parse.GetValue(json), token);
            PreviewPreparationResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var key = Input(product);
                var tenantId = Guid.Parse(Input(tenant)).ToString();
                var subscriptionId = Guid.Parse(Input(subscription)).ToString();
                var inspect = parse.GetValue(status);
                var continuing = parse.GetValue(resume);
                var configuring = parse.GetValue(configure);
                var approved = parse.GetValue(approval)?.Trim();
                if (inspect && (continuing || approved is not null || parse.GetValue(configuration) is not null)
                    || continuing && parse.GetValue(configuration) is not null
                    || configuring && (continuing || parse.GetValue(configuration) is not null)
                    || approved is not null && !FactorySourceValidation.Hash(approved))
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "ConflictingPreviewModeOrInvalidApproval; no provider access.");
                PreviewPreparationSelection? selection = null;
                if (!inspect && !continuing && !configuring)
                {
                    var input = Input(configuration);
                    if (System.Text.Encoding.UTF8.GetByteCount(input) > 16 * 1024) throw new JsonException();
                    selection = JsonSerializer.Deserialize<PreviewPreparationSelection>(input, FactorySourceValidation.RecordJson) ?? throw new JsonException();
                    FactoryPreviewPreparation.Validate(selection);
                }
                if (continuing && approved is null) approved = Input(approval);
                if (approved is not null && !FactorySourceValidation.Hash(approved)) throw new JsonException();
                _ = new ProvisioningAssets().Resolve("onboard-preview-workloads.json");
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var reviews = reviewer ?? RepositoryAttachmentReviewer.Create(runner);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(configuring
                    ? FactoryPreviewPublication.Policy.RequestSeconds : FactoryPreviewPreparation.Policy.RequestSeconds), time);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var transport = http ?? PreviewPreparationHttp;
                var reservations = new FactoryReservationService(reviews, runner, transport, time);
                var admission = await reservations.StatusAsync(owner, tenantId, subscriptionId, key, pending.Token);
                var service = new FactoryPreviewPreparationService(reviews, runner, transport, time);
                if (configuring)
                {
                    result = await service.ConfigureAsync(admission, approved, inspect, pending.Token);
                    if (interactive && !inspect && approved is null && result.Publication is not null)
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
                        approved = terminal.Prompt("Type this exact publication approvalFingerprint; blank leaves configuration read-only: ")?.Trim();
                        terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities,
                            $"dsf onboard decide preview --owner-appconfig-endpoint {owner} --product {key} --tenant {tenantId} --subscription {subscriptionId} --configure"
                            + (string.IsNullOrEmpty(approved) ? "" : " --approve " + ReviewText(approved))));
                        if (!string.IsNullOrEmpty(approved)) result = await service.ConfigureAsync(admission, approved, false, pending.Token);
                    }
                }
                else if (inspect) result = await service.InspectAsync(admission, pending.Token);
                else if (approved is not null) result = await service.PrepareAsync(admission, selection, approved, continuing, pending.Token);
                else
                {
                    var plan = await service.PlanAsync(admission, selection!, pending.Token);
                    result = new(PreviewPreparationState.Reviewed, "Exact read-only native preparation plan; no effects or readiness.", plan);
                    if (interactive)
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
                        approved = terminal.Prompt("Type the exact approvalFingerprint to provision only these native resources; blank leaves this read-only: ")?.Trim();
                        string[] equivalent = ["dsf", "onboard", "decide", "preview", "--owner-appconfig-endpoint", owner,
                            "--product", key, "--tenant", tenantId, "--subscription", subscriptionId,
                            "--preview-config", JsonSerializer.Serialize(selection, FactoryGitHubJson.Options),
                            .. string.IsNullOrEmpty(approved) ? Array.Empty<string>() : new[] { "--approve", approved }];
                        terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, ReviewText(string.Join(" ",
                            equivalent.Select(s => System.Text.RegularExpressions.Regex.IsMatch(s, "^[-A-Za-z0-9_./:@]+$") ? s
                                : "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                        if (!string.IsNullOrEmpty(approved))
                            result = await service.PrepareAsync(admission, selection, approved, false, pending.Token);
                    }
                }
                if (result.State is PreviewPreparationState.Reviewed or PreviewPreparationState.ConfirmedNotReady
                    or PreviewPreparationState.SeedAwaitingApproval or PreviewPreparationState.ConfiguredAwaitingEvaluation)
                {
                    var current = await reservations.StatusAsync(owner, tenantId, subscriptionId, key, pending.Token);
                    if (current.State != FactoryReservationState.Reserved || current.Identity != admission.Identity
                        || current.Foundation?.State != FactoryFoundationState.Inactive)
                        result = result with { State = PreviewPreparationState.Unknown, Detail = "FinalOwnerRegistryAuthorityUnconfirmed; retained preparation is not current readiness." };
                }
            }
            catch (ReservationStoreException e)
            {
                result = new(e.State == FactoryReservationState.Unknown ? PreviewPreparationState.Unknown
                    : e.State == FactoryReservationState.Canceled ? PreviewPreparationState.Canceled : PreviewPreparationState.Blocked, e.Message);
            }
            catch (PreviewCapabilityException e)
            {
                result = new(e.State == FactoryCharterState.Unknown
                    ? PreviewPreparationState.Unknown : PreviewPreparationState.Blocked, e.Message);
            }
            catch (OperationCanceledException)
            { result = new(PreviewPreparationState.Canceled, "Preparation canceled; inspect retained native history before any continuation."); }
            catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidOperationException
                or KeyNotFoundException or HttpRequestException or IOException or OwnerReservationStore.AuthenticationFailedException
                or CharterAdoptionEvidenceException or CharterOperationException or ModelCapabilityException)
            { result = new(PreviewPreparationState.Blocked, "Complete explicit preview selection and current adopted/source/model/telemetry/selected-source authority required. Provider details withheld."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State is PreviewPreparationState.Reviewed or PreviewPreparationState.ConfirmedNotReady
                or PreviewPreparationState.SeedAwaitingApproval or PreviewPreparationState.ConfiguredAwaitingEvaluation ? Success
                : result.State == PreviewPreparationState.Canceled ? CanceledExitCode : Failure;

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
}
