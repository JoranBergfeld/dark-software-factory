using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildSourceCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("source", "advance bounded pinned acquisition; --prepare reviews its isolated host/store; never analyze");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var prepare = BoolOption("--prepare", "review/create only the source workload and source-only progress storage");
        var image = StringOption("--runtime-image", "implemented runtime image pinned by digest");
        var audience = StringOption("--source-audience", "existing Entra API client ID; native operator/code consent required");
        var approval = StringOption("--approve", "exact reviewed source preparation fingerprint");
        var status = BoolOption("--status-only", "read trusted remote coverage without advancing");
        var startNew = BoolOption("--new-snapshot", "explicitly replace current snapshot selection; retain bounded historical metadata");
        var page = IntOption("--page", "zero-based coverage page");
        var json = BoolOption("--json", "structured nonsecret coverage; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, prepare, image, audience, approval, status, startNew, page, json);
        command.SetAction(async (parse, token) =>
        {
            object output;
            var exit = Failure;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                if (parse.GetValue(prepare) && (parse.GetValue(status) || parse.GetValue(startNew))
                    || !parse.GetValue(prepare) && (parse.GetValue(image) is not null || parse.GetValue(audience) is not null || parse.GetValue(approval) is not null)
                    || parse.GetValue(status) && parse.GetValue(startNew))
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "Preparation, acquisition and status are separate operations; incompatible flags rejected.");
                if (parse.GetValue(prepare))
                {
                    try { _ = new ProvisioningAssets().Resolve("onboard-source.json"); }
                    catch (InvalidOperationException)
                    { throw new ReservationStoreException(FactoryReservationState.Failed,
                        "Packaged onboard-source.json is missing, unreadable or invalid; reinstall the CLI. No provider access attempted."); }
                }
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint) ?? "");
                var productKey = Input(product) ?? "";
                var tenantId = Guid.Parse(Input(tenant) ?? "").ToString();
                var subscriptionId = Guid.Parse(Input(subscription) ?? "").ToString();
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var admission = await new FactoryReservationService(reviewer, runner, http, time).StatusAsync(owner, tenantId, subscriptionId, productKey, token);
                var service = new FactorySourceService(runner, http, time);
                if (parse.GetValue(prepare))
                {
                    var selection = new SourceSelection(Input(image) ?? "", Input(audience) ?? "");
                    var preview = await service.PreviewAsync(admission, selection, token);
                    var approved = parse.GetValue(approval);
                    if (interactive && string.IsNullOrWhiteSpace(approved))
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(preview, ReviewJson));
                        approved = terminal.Prompt("Approve exact code identity, private source-only store, grants and host: fingerprint (blank = preview): ")?.Trim();
                        if (!string.IsNullOrWhiteSpace(approved) && !FactorySourceValidation.Hash(approved))
                            throw new ReservationStoreException(FactoryReservationState.Blocked, "Approval must be the exact displayed fingerprint; no effects.");
                        terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, ReviewText(
                            "dsf onboard decide source --prepare --owner-appconfig-endpoint " + owner + " --product " + productKey
                            + " --tenant " + tenantId + " --subscription " + subscriptionId + " --runtime-image " + selection.RuntimeImage
                            + " --source-audience " + selection.AudienceClientId + (string.IsNullOrWhiteSpace(approved) ? "" : " --approve " + approved))));
                    }
                    output = string.IsNullOrWhiteSpace(approved)
                        ? admission with { State = FactoryReservationState.Partial, Source = preview, Detail = preview.Detail }
                        : await service.PrepareAsync(admission, preview, approved, token);
                }
                else
                {
                    var result = await service.AcquireAsync(admission, new(parse.GetValue(startNew), parse.GetValue(page) ?? 0), parse.GetValue(status), token);
                    output = result;
                    exit = result.State == SourceState.Acquired ? Success : result.State == SourceState.Canceled ? CanceledExitCode : Failure;
                }
            }
            catch (ReservationStoreException e) { output = new FactoryReservationResult(e.State, e.Message); }
            catch (OperationCanceledException)
            { output = new SourceInventoryResult(SourceState.Canceled, "Canceled; remote progress/effects/hold may be unconfirmed. Inspect trusted status; never recreate infrastructure.", null, [], null); exit = CanceledExitCode; }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or HttpRequestException or FormatException
                or OwnerReservationStore.AuthenticationFailedException)
            { output = new SourceInventoryResult(SourceState.Blocked, "Current approved source authority or provider metadata unavailable; no source/analysis result, public-access fallback or privilege expansion.", null, [], null); }
            terminal.WriteLine(JsonSerializer.Serialize(output, ReviewJson));
            return output is FactoryReservationResult { State: FactoryReservationState.Canceled } ? CanceledExitCode : exit;

            string? Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrWhiteSpace(value) && interactive) value = terminal.Prompt(option.Name + " (" + option.Description + "): ")?.Trim();
                return value;
            }
        });
        return command;
    }
}
