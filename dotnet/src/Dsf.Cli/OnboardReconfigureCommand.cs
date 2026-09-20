using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildReconfigureCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("reconfigure", "read-only retained factory inputs and exact change diff; applying changes is not yet available");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "retained product");
        var tenant = StringOption("--tenant", "retained tenant");
        var subscription = StringOption("--subscription", "retained subscription");
        var inspect = BoolOption("--inspect", "read complete authoritative current inputs, revisions and protected references");
        var input = StringOption("--input", "complete version-1 desired-input JSON file; explicit null removes all supplemental sources");
        var fingerprint = StringOption("--reviewed-fingerprint", "require this exact current read-only review; never approves application");
        var json = BoolOption("--json", "structured nonsecret review; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, inspect, input, fingerprint, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryReconfigurationReview result;
            try
            {
                var inspection = parse.GetValue(inspect);
                var path = parse.GetValue(input);
                var pin = parse.GetValue(fingerprint);
                if (inspection == !string.IsNullOrWhiteSpace(path) || inspection && pin is not null
                    || pin is not null && !FactorySourceValidation.Hash(pin))
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Use exactly --inspect or --input FILE. Replay requires a complete exact --reviewed-fingerprint, never yes or approval.");
                var owner = FactoryGovernance.NormalizeEndpoint(Required(endpoint));
                var tenantId = BoundarySelectionInput.GuidValue(Required(tenant), "--tenant");
                var subscriptionId = BoundarySelectionInput.GuidValue(Required(subscription), "--subscription");
                var productKey = BoundarySelectionInput.Name(Required(product), "--product");
                FactoryReconfigurationInputs? desired = null;
                if (!inspection)
                {
                    if (new FileInfo(path!).Length > FactoryReservationRecords.MaxPlanBytes)
                        throw new ReservationStoreException(FactoryReservationState.Blocked, "Complete desired input exceeds 1 MiB; no truncation.");
                    desired = JsonSerializer.Deserialize<FactoryReconfigurationInputs>(
                        await File.ReadAllTextAsync(path!, token), FactorySourceValidation.RecordJson) ?? throw new JsonException();
                }
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var admission = await new FactoryReservationService(reviewer, runner, http, time)
                    .StatusAsync(owner, tenantId, subscriptionId, productKey, token);
                result = await new FactoryReconfigurationService(reviewer, runner, http, time).ReviewAsync(admission, desired, pin, token);
            }
            catch (ReservationStoreException e) { result = new(Enum.Parse<FactoryReconfigurationState>(e.State.ToString()), e.Message); }
            catch (FactoryAuditException e) { result = new(FactoryReconfigurationState.Unknown, "Protected evidence closure unavailable: " + e.Message); }
            catch (ModelCapabilityException e) { result = new(FactoryReconfigurationState.Blocked, "Model selection: " + e.Reason + ". " + e.Message); }
            catch (PreviewCapabilityException e) { result = new(FactoryReconfigurationState.Blocked, e.Message); }
            catch (BoundaryInputException e) { result = new(FactoryReconfigurationState.Blocked, e.Message); }
            catch (BoundaryInspectionException) { result = new(FactoryReconfigurationState.Unknown, "Current boundary authority unavailable; absence was not established."); }
            catch (OperationCanceledException) { result = new(FactoryReconfigurationState.Canceled, "Read-only review canceled; no effects dispatched."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException
                or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { result = new(FactoryReconfigurationState.Blocked, "Complete supported desired inputs and intact authoritative records required; provider/input contents withheld."); }
            catch (Exception e) when (e is HttpRequestException or OwnerReservationStore.AuthenticationFailedException)
            { result = new(FactoryReconfigurationState.Unknown, "Current authority unavailable; no fallback, changes or release."); }
            if (!parse.GetValue(json))
                terminal.WriteLine("[dsf] Read-only reconfiguration review. No application, claim, grant, approval or workload changes.");
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State is FactoryReconfigurationState.Inspected or FactoryReconfigurationState.Reviewed ? Success
                : result.State == FactoryReconfigurationState.Canceled ? CanceledExitCode : Failure;

            string Required(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrWhiteSpace(value) && terminal.Capabilities.IsInteractive && !parse.GetValue(json))
                    value = terminal.Prompt(option.Name + " (" + option.Description + "): ")?.Trim();
                return string.IsNullOrWhiteSpace(value) ? throw new ReservationStoreException(FactoryReservationState.Blocked,
                    option.Name + " required; current authority is never inferred from local projections.") : value;
            }
        });
        return command;
    }
}
