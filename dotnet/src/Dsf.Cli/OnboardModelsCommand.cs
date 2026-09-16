using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildModelsCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock, HttpMessageHandler? modelProofTransport)
    {
        var command = new Command("models", "prepare and prove required factory model capabilities; never activate");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var topology = StringOption("--model-topology", "explicit JSON model-serving topology; no secrets");
        var approval = StringOption("--approve", "exact reviewed model capability fingerprint");
        var verify = BoolOption("--verify-only", "hold then prove current hosted model routes; no creation");
        var json = BoolOption("--json", "structured result; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, topology, approval, verify, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryReservationResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var input = parse.GetValue(verify) ? null : Input(topology);
                if (!parse.GetValue(verify) && string.IsNullOrWhiteSpace(input))
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "--model-topology required: review factory synthesis/embedding deployments and three existing diverse Azure jurors.");
                if (parse.GetValue(verify) && (parse.GetValue(topology) is not null || parse.GetValue(approval) is not null))
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "--verify-only consumes existing reviewed configuration; omit topology/approval.");
                if (!parse.GetValue(verify))
                {
                    try { _ = new ProvisioningAssets().Resolve("onboard-models.json"); }
                    catch (InvalidOperationException)
                    { throw new ReservationStoreException(FactoryReservationState.Failed,
                        "Packaged onboard-models.json is missing, unreadable or invalid; reinstall the CLI. No provider access attempted."); }
                }
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint) ?? "");
                var tenantId = Guid.Parse(Input(tenant) ?? "").ToString();
                var subscriptionId = Guid.Parse(Input(subscription) ?? "").ToString();
                var productKey = Input(product) ?? "";
                result = await new FactoryReservationService(reviewer, runner, http, time).StatusAsync(
                    owner, tenantId, subscriptionId, productKey, token);
                var service = new FactoryModelsService(runner, http, time, modelProofTransport);
                if (parse.GetValue(verify)) result = await service.CheckAsync(result, token);
                else if (result.Models is not null)
                    result = result with { State = FactoryReservationState.Blocked, Detail = "Model preparation already held; inspect current records, no create retry or replacement." };
                else
                {
                    var selection = JsonSerializer.Deserialize<ModelTopologySelection>(input!, FactoryGitHubJson.Options) ?? throw new JsonException();
                    var preview = await service.PreviewAsync(result, selection, token);
                    var approved = parse.GetValue(approval);
                    if (interactive && string.IsNullOrWhiteSpace(approved))
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(preview, ReviewJson));
                        approved = terminal.Prompt("Approve exact model deployments, reused jurors, grants and private host: fingerprint (blank = preview): ")?.Trim();
                        string[] equivalent = ["dsf", "onboard", "decide", "models", "--owner-appconfig-endpoint", owner, "--product", productKey,
                            "--tenant", tenantId, "--subscription", subscriptionId, "--model-topology", JsonSerializer.Serialize(selection, FactoryGitHubJson.Options),
                            .. string.IsNullOrWhiteSpace(approved) ? Array.Empty<string>() : new[] { "--approve", approved }];
                        terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, ReviewText(string.Join(" ",
                            equivalent.Select(s => System.Text.RegularExpressions.Regex.IsMatch(s, "^[-A-Za-z0-9_./:@]+$") ? s : "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                    }
                    result = string.IsNullOrWhiteSpace(approved)
                        ? result with { State = FactoryReservationState.Partial, Models = preview, Detail = preview.Detail }
                        : await service.ApplyAsync(result, preview, approved, token);
                }
            }
            catch (ModelCapabilityException e) { result = new(FactoryReservationState.Blocked, $"{e.Route}: {e.Reason}. {e.Message}"); }
            catch (ReservationStoreException e) { result = new(e.State, e.Message); }
            catch (OperationCanceledException) { result = new(FactoryReservationState.Canceled, "Model capability canceled; no confirmed hold or revocation asserted."); }
            catch (HttpRequestException) { result = new(FactoryReservationState.Blocked, "Model metadata network unavailable; no public-access relaxation or fallback."); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            { result = new(FactoryReservationState.Blocked, "Malformed or unsupported model topology/native metadata; model administrator must inspect. No secrets or raw provider response released."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReservationState.Reserved && result.Models?.State == ModelCapabilityState.Verified ? Success
                : result.State == FactoryReservationState.Canceled ? CanceledExitCode : Failure;

            string? Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrWhiteSpace(value) && interactive)
                    value = terminal.Prompt(option.Name + " (" + option.Description + "): ")?.Trim();
                return value;
            }
        });
        return command;
    }
}
