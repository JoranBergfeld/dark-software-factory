using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildSupplementalCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var json = BoolOption("--json", "structured result; never prompt");
        var input = StringOption("--input", "exact reviewed optional source selection JSON; never contains a secret value");
        var approval = StringOption("--approve", "exact displayed supplemental selection fingerprint");
        var verify = BoolOption("--verify-only", "hold and recheck every selected deployed source; never recreate");
        var command = new Command("supplemental", "explicit optional evidence sources; never replace application telemetry or activate");
        AddOptions(command, endpoint, product, tenant, subscription, json, input, approval, verify);
        command.SetAction(async (parse, token) =>
        {
            FactoryReservationResult result;
            try
            {
                var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
                if (new[] { input, approval }.Any(option => parse.GetValue(option) is { } supplied && string.IsNullOrWhiteSpace(supplied)))
                    throw new BoundaryInputException("Explicit --input and --approve values must be nonblank; omission or invalid input never removes an approved source.");
                if (parse.GetValue(verify) && (parse.GetValue(input) is not null || parse.GetValue(approval) is not null)
                    || parse.GetValue(approval) is not null && parse.GetValue(input) is null)
                    throw new BoundaryInputException("--approve requires --input; --verify-only forbids new input or approval. No implicit reconfiguration.");
                string Input(Option<string> option) => parse.GetValue(option)?.Trim()
                    ?? (interactive ? terminal.Prompt(option.Name + ": ")?.Trim() : null)
                    ?? throw new BoundaryInputException(option.Name + " required; no default authority.");
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var key = Input(product);
                var tenantId = Guid.Parse(Input(tenant)).ToString();
                var subscriptionId = Guid.Parse(Input(subscription)).ToString();
                result = await new FactoryReservationService(reviewer, azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System)
                    .StatusAsync(owner, tenantId, subscriptionId, key, token);
                await FactoryReconfigurationGate.RequireCapabilityAsync(result);
                if (result.State != FactoryReservationState.Reserved || result.Foundation?.State != FactoryFoundationState.Inactive)
                    throw new ReservationStoreException(result.State == FactoryReservationState.Reserved ? FactoryReservationState.Blocked : result.State,
                        "Proven current inactive foundation and intact selected-source authority required; no Azure-only downgrade.");
                var service = new FactorySupplementalService(azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System);
                var path = parse.GetValue(input);
                var approved = parse.GetValue(approval);
                if (interactive && path is null && !parse.GetValue(verify) && result.Supplemental is null)
                    path = terminal.Prompt("Optional source selection JSON path (blank = Azure-only; never auto-enable): ")?.Trim();
                if (parse.GetValue(verify)) result = await service.CheckAsync(result, token);
                else if (!string.IsNullOrWhiteSpace(path))
                {
                    var selection = await ReadSupplementalInputAsync(path, result.Identity!.SubscriptionId, token);
                    if (result.Supplemental is not null)
                        throw new ReservationStoreException(FactoryReservationState.Blocked,
                            "Approved supplemental selection already exists; use --verify-only without input. Changing or removing it requires separate review; no replacement.");
                    var preview = await service.PreviewAsync(result, selection, token);
                    if (interactive && approved is null)
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(preview, ReviewJson));
                        approved = terminal.Prompt("Approve exact supplemental hosts, identities, data scopes and public-safe queries: fingerprint (blank = preview): ")?.Trim();
                    }
                    if (interactive) Equivalent();
                    result = string.IsNullOrWhiteSpace(approved) ? result with { Supplemental = preview }
                        : await service.PrepareAsync(result, preview, approved, token);
                }
                else if (result.Supplemental is null)
                {
                    await service.RequireUnselectedAsync(result, token);
                    result = result with { Supplemental = new(SupplementalState.NotSelected,
                        "Azure-only: no supplemental selection, secret, workload or data access required. Fresh dedicated application telemetry remains mandatory.") };
                }
                if (interactive && string.IsNullOrWhiteSpace(path)) Equivalent();

                void Equivalent()
                {
                    string[] args = ["dsf", "onboard", "decide", "supplemental", "--owner-appconfig-endpoint", owner, "--product", key,
                        "--tenant", tenantId, "--subscription", subscriptionId,
                        .. string.IsNullOrWhiteSpace(path) ? Array.Empty<string>() : new[] { "--input", path },
                        .. string.IsNullOrWhiteSpace(approved) ? Array.Empty<string>() : new[] { "--approve", approved },
                        .. parse.GetValue(verify) ? new[] { "--verify-only" } : Array.Empty<string>()];
                    terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, ReviewText(string.Join(' ',
                        args.Select(a => System.Text.RegularExpressions.Regex.IsMatch(a, "^[-A-Za-z0-9_./:@]+$") ? a
                            : "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                }
            }
            catch (BoundaryInputException e) { result = new(FactoryReservationState.Failed, e.Message); }
            catch (ReservationStoreException e) { result = new(e.State, e.Message); }
            catch (OperationCanceledException) { result = new(FactoryReservationState.Canceled, "Canceled; no rollback or revocation asserted."); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or IOException or SupplementalSourceException)
            { result = new(FactoryReservationState.Unknown, "Required supplemental authority unavailable; no fallback. Administrator must inspect."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReservationState.Canceled ? CanceledExitCode
                : result.State == FactoryReservationState.Reserved
                    && result.Supplemental?.State is SupplementalState.NotSelected or SupplementalState.ReadySupplemental ? Success : Failure;
        });
        return command;
    }

    private static async Task<SupplementalSelection> ReadSupplementalInputAsync(string path, string subscription, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var bytes = new byte[8193];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token);
            if (count > 8192) throw new BoundaryInputException("--input exceeds 8192 bytes; no effects or truncation.");
            var selection = JsonSerializer.Deserialize<SupplementalSelection>(bytes.AsSpan(0, count), FactoryGitHubJson.Options) ?? throw new JsonException();
            FactorySupplementalValidation.ValidateSelection(selection, subscription);
            return selection;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new BoundaryInputException("--input requires valid version-1 FoundryIQ/WebIQ sources, explicit publicQueryApproved, a digest-pinned runtime image, "
                + "existing audience, and exact product secret version or existing dedicated Search KB/source/index scopes and ETags. Empty or implicit selections unsupported; no effects.");
        }
    }
}
