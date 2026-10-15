using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildFoundationCommand(
        ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer, IAzureCliRunner? azure,
        HttpClient? http, TimeProvider? clock)
    {
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner reservation endpoint");
        var product = StringOption("--product", "reserved product key");
        var tenant = StringOption("--tenant", "reserved tenant ID");
        var subscription = StringOption("--subscription", "reserved subscription ID");
        var fingerprint = StringOption("--reviewed-fingerprint", "separate approval of the reserved full plan for INACTIVE foundation provisioning");
        var json = BoolOption("--json", "nonsecret foundation state; never prompt");
        var command = new Command("provision", "provision only the approved inactive foundation; never activate");
        AddOptions(command, endpoint, product, tenant, subscription, fingerprint, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryReservationResult result;
            FactoryReservationResult? observed = null;
            try
            {
                var approval = parse.GetValue(fingerprint);
                var prompted = string.IsNullOrEmpty(approval) && terminal.Capabilities.IsInteractive && !parse.GetValue(json);
                if (prompted)
                    approval = terminal.Prompt("Approve INACTIVE foundation only: enter the reserved full plan fingerprint: ")?.Trim();
                if (approval is null || !Regex.IsMatch(approval, "^[0-9a-fA-F]{64}$"))
                    throw new BoundaryInputException("--reviewed-fingerprint is required; reservation is not provisioning approval.");
                var owner = parse.GetValue(endpoint)?.Trim();
                try { new RepositoryAttachmentReviewRequest("owner/repository", owner, null).Validate(); }
                catch (InvalidOperationException e) { throw new BoundaryInputException(e.Message); }
                if (string.IsNullOrEmpty(owner) || !Guid.TryParse(parse.GetValue(tenant), out var tenantId) || tenantId == Guid.Empty
                    || !Guid.TryParse(parse.GetValue(subscription), out var subscriptionId) || subscriptionId == Guid.Empty
                    || parse.GetValue(product) is not { } key || !Regex.IsMatch(key, "^[a-z0-9][a-z0-9_-]{0,89}$"))
                    throw new BoundaryInputException("--owner-appconfig-endpoint, --product, --tenant and --subscription are required.");
                if (prompted)
                    terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities,
                        $"dsf onboard decide provision --owner-appconfig-endpoint {new Uri(owner).GetLeftPart(UriPartial.Authority).ToLowerInvariant()} "
                        + $"--product {key} --tenant {tenantId:D} --subscription {subscriptionId:D} --reviewed-fingerprint {approval.ToLowerInvariant()}"));
                try { _ = new ProvisioningAssets().Resolve("onboard-foundation.json"); }
                catch (InvalidOperationException)
                { throw new BoundaryInputException("Packaged onboard-foundation.json is missing, unreadable or invalid; reinstall the CLI. No provider access attempted."); }
                var service = new FactoryReservationService(reviewer, azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System);
                result = await service.StatusAsync(new Uri(owner).GetLeftPart(UriPartial.Authority).ToLowerInvariant(),
                    tenantId.ToString(), subscriptionId.ToString(), key, token);
                observed = result;
                if (result.Identity?.PlanFingerprint != approval.ToLowerInvariant())
                    result = result with { State = FactoryReservationState.Blocked, Detail = "Approval differs from the remotely admitted plan; no dispatch." };
                else if (result.Foundation is null && result.State == FactoryReservationState.Reserved)
                {
                    result = await service.ResumeAsync(new Uri(owner).GetLeftPart(UriPartial.Authority).ToLowerInvariant(),
                        tenantId.ToString(), subscriptionId.ToString(), key, null, token);
                    result = await new FactoryFoundationService(azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System)
                        .ProvisionAsync(result, token);
                }
            }
            catch (BoundaryInputException e) { result = new(FactoryReservationState.Failed, e.Message); }
            catch (ReservationStoreException e) { result = new(e.State, e.Message); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { result = new(FactoryReservationState.Canceled, "Canceled; new dispatch closed. Inspect remote progress."); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
                or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
            {
                const string detail = "Foundation authority/input/provider data could not be verified; no further dispatch. Details withheld.";
                result = (observed ?? new(FactoryReservationState.Unknown, detail)) with
                {
                    State = FactoryReservationState.Unknown,
                    Detail = detail,
                    Plan = null,
                    Foundation = observed?.Foundation is { } foundation ? foundation with { State = FactoryFoundationState.Unknown, Detail = detail } : null,
                };
            }
            if (!parse.GetValue(json)) terminal.WriteLine(ReviewText($"[dsf] {result.State}: {result.Detail}"));
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReservationState.Reserved && result.Foundation?.State == FactoryFoundationState.Inactive ? Success
                : result.State == FactoryReservationState.Canceled ? CanceledExitCode : Failure;
        });
        return command;
    }
}
