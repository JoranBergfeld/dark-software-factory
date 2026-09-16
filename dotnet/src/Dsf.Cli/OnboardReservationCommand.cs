using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildReservationCommand(
        string action, ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var input = StringOption("--input", "complete reviewed version-1 selection JSON file");
        var fingerprint = StringOption("--reviewed-fingerprint", "explicit approval of the exact current full plan fingerprint");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted prepared owner App Configuration endpoint");
        var product = StringOption("--product", "owner-scoped product key");
        var tenant = StringOption("--tenant", "explicit owner reservation tenant ID");
        var subscription = StringOption("--subscription", "explicit owner reservation subscription ID");
        var json = BoolOption("--json", "print nonsecret typed reservation/foundation state without prompts");
        var command = new Command(action, action == "reserve"
            ? "reserve durable factory identity and owner-scoped claims; never provision"
            : action == "resume" ? "resume the reservation or inspect a dispatched foundation; never reissue ARM"
            : "inspect trusted reservation and product foundation authority; read-only");
        if (action == "reserve") AddOptions(command, input, fingerprint, json);
        else AddOptions(command, endpoint, product, tenant, subscription, json);
        if (action == "resume") AddOptions(command, input);
        command.SetAction(async (parse, token) =>
        {
            FactoryReservationResult result;
            try
            {
                var service = new FactoryReservationService(reviewer, azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System);
                if (action == "reserve")
                {
                    var path = parse.GetValue(input);
                    var approval = parse.GetValue(fingerprint);
                    if (string.IsNullOrWhiteSpace(path) || approval is null || !Regex.IsMatch(approval, "^[0-9a-fA-F]{64}$"))
                        throw new BoundaryInputException("--input and --reviewed-fingerprint are required; explicit approval applies only to reservation, not provisioning or activation.");
                    var selection = JsonSerializer.Deserialize<ApplicationBoundarySelection>(
                        await File.ReadAllTextAsync(path, token), BoundaryInputJson) ?? throw new JsonException();
                    result = await service.ReserveAsync(selection, approval, token);
                }
                else
                {
                    var owner = parse.GetValue(endpoint)?.Trim();
                    try { new RepositoryAttachmentReviewRequest("owner/repository", owner, null).Validate(); }
                    catch (InvalidOperationException e) { throw new BoundaryInputException(e.Message); }
                    if (string.IsNullOrEmpty(owner) || !Guid.TryParse(parse.GetValue(tenant), out var tenantId) || tenantId == Guid.Empty
                        || !Guid.TryParse(parse.GetValue(subscription), out var subscriptionId) || subscriptionId == Guid.Empty
                        || parse.GetValue(product) is not { } key || !Regex.IsMatch(key, "^[a-z0-9][a-z0-9_-]{0,89}$"))
                        throw new BoundaryInputException("--owner-appconfig-endpoint, --product, --tenant and --subscription are required; local files are not authority.");
                    var ownerEndpoint = new Uri(owner).GetLeftPart(UriPartial.Authority).ToLowerInvariant();
                    var repairInput = parse.GetValue(input) is { } repairPath
                        ? JsonSerializer.Deserialize<ApplicationBoundarySelection>(await File.ReadAllTextAsync(repairPath, token), BoundaryInputJson) ?? throw new JsonException()
                        : null;
                    result = action == "resume"
                        ? await service.ResumeAsync(ownerEndpoint, tenantId.ToString(), subscriptionId.ToString(), key, repairInput, token)
                        : await service.StatusAsync(ownerEndpoint, tenantId.ToString(), subscriptionId.ToString(), key, token);
                }
            }
            catch (ReservationStoreException e) { result = new(e.State, e.Message); }
            catch (BoundaryInspectionException e) { result = new(FactoryReservationState.Unknown, e.Message); }
            catch (BoundaryInputException e) { result = new(FactoryReservationState.Failed, e.Message); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            { result = new(FactoryReservationState.Failed, "Complete readable version-1 selection required; input/provider details withheld."); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { result = new(FactoryReservationState.Canceled, "Canceled; no further dispatch. Inspect remote status before resuming."); }
            if (!parse.GetValue(json))
                terminal.WriteLine(ReviewText($"[dsf] {result.State}: {result.Detail}"));
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReservationState.Reserved ? Success : result.State == FactoryReservationState.Canceled ? CanceledExitCode : Failure;
        });
        return command;
    }
}
