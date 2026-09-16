using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildTelemetryCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var freshness = StringOption("--freshness-hours", "reviewed positive freshness window; default 24 hours");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var json = BoolOption("--json", "structured result; never prompt");
        var image = StringOption("--runtime-image", "implemented runtime image pinned by sha256");
        var audience = StringOption("--auth-audience", "existing approved Entra API client ID");
        var approval = StringOption("--approve", "exact displayed telemetry configuration fingerprint");
        var verifyOnly = BoolOption("--verify-only", "hold readiness then recheck existing grants, host and actual workload; no creation retry");
        var command = new Command("telemetry", "prepare and check isolated application telemetry; never activate");
        AddOptions(command, freshness, endpoint, product, tenant, subscription, json, image, audience, approval, verifyOnly);
        command.SetAction(async (parse, token) =>
        {
            var hours = parse.GetValue(freshness) ?? "24";
            var valid = double.TryParse(hours, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value * 3600 >= 1 && value <= 24 * 365 && value * 3600 == Math.Truncate(value * 3600);
            FactoryReservationResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            var prompted = false;
            try
            {
                if (!valid) throw new BoundaryInputException("--freshness-hours must be positive whole seconds, finite and at most 8760 hours.");
                if (parse.GetValue(verifyOnly) && new[] { freshness, image, audience, approval }.Any(o => parse.GetValue(o) is not null))
                    throw new BoundaryInputException("--verify-only uses current reviewed configuration; omit new inputs/approval.");
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint) ?? "");
                var key = Input(product) ?? throw new JsonException();
                var tenantId = Guid.Parse(Input(tenant) ?? "").ToString();
                var subscriptionId = Guid.Parse(Input(subscription) ?? "").ToString();
                if (!parse.GetValue(verifyOnly))
                {
                    try { _ = new ProvisioningAssets().Resolve("onboard-telemetry.json"); }
                    catch (InvalidOperationException)
                    { throw new BoundaryInputException("Packaged onboard-telemetry.json is missing, unreadable or invalid; reinstall the CLI. No provider access attempted."); }
                }
                result = await new FactoryReservationService(reviewer, azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System)
                    .StatusAsync(owner, tenantId, subscriptionId, key, token);
                var service = new FactoryTelemetryService(azure ?? new SystemAzureCliRunner(), http, clock ?? TimeProvider.System);
                if (parse.GetValue(verifyOnly)) result = await service.CheckAsync(result, token);
                else if (result.Telemetry is null)
                {
                    var selectedImage = Input(image);
                    var selectedAudience = Input(audience);
                    if (interactive && parse.GetValue(freshness) is null)
                    {
                        prompted = true;
                        var answer = terminal.Prompt("Positive freshness hours (blank = explicitly review default 24h; no cadence assumption): ")?.Trim();
                        if (!string.IsNullOrEmpty(answer))
                        {
                            if (!double.TryParse(answer, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
                                || !double.IsFinite(value) || value * 3600 < 1 || value > 8760 || value * 3600 != Math.Truncate(value * 3600))
                                throw new BoundaryInputException("--freshness-hours must be positive whole seconds and at most 8760 hours.");
                        }
                    }
                    var preview = FactoryTelemetryService.Preview(result, selectedImage, selectedAudience, (int)(value * 3600));
                    var approved = parse.GetValue(approval);
                    if (string.IsNullOrWhiteSpace(approved) && interactive)
                    {
                        prompted = true;
                        terminal.WriteLine(JsonSerializer.Serialize(preview, ReviewJson));
                        approved = terminal.Prompt("Approve exact source/backend/config grants, native queries (including metadata-only stale probe), image and freshness: fingerprint (blank = preview): ")?.Trim();
                    }
                    if (prompted)
                    {
                        string[] args = ["dsf", "onboard", "decide", "telemetry", "--owner-appconfig-endpoint", owner, "--product", key,
                            "--tenant", tenantId, "--subscription", subscriptionId, "--runtime-image", selectedImage!, "--auth-audience", selectedAudience!,
                            "--freshness-hours", value.ToString(CultureInfo.InvariantCulture),
                            .. string.IsNullOrWhiteSpace(approved) ? Array.Empty<string>() : new[] { "--approve", approved }];
                        terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities,
                            ReviewText(string.Join(' ', args.Select(a => Regex.IsMatch(a, @"^[-A-Za-z0-9_./:@]+$") ? a : "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'")))));
                    }
                    result = string.IsNullOrWhiteSpace(approved)
                        ? result with { State = FactoryReservationState.Partial, Telemetry = preview, Detail = preview.Detail + " " + FactoryTelemetryValidation.Coverage }
                        : await service.ApplyAsync(result, preview, approved, token);
                }
                else if (new[] { image, audience, freshness, approval }.Any(o => parse.GetValue(o) is not null))
                    result = result with { State = FactoryReservationState.Blocked,
                        Detail = "Preparation already exists; inspect or --verify-only without new inputs. No reconfiguration, replacement or unsafe retry." };
            }
            catch (BoundaryInputException e) { result = new(FactoryReservationState.Failed, e.Message); }
            catch (ReservationStoreException e) { result = new(e.State, e.Message); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { result = new(FactoryReservationState.Canceled, parse.GetValue(verifyOnly)
                ? FactoryTelemetryService.NoConfirmedHold : "Canceled; effects remain inspectable, no rollback."); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
            { result = new(FactoryReservationState.Unknown, "Trusted telemetry selection/authority unavailable; administrator must inspect, no effects or fallback."); }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State == FactoryReservationState.Reserved && result.Telemetry?.State == TelemetryState.Ready ? Success
                : result.State == FactoryReservationState.Canceled ? CanceledExitCode : Failure;

            string? Input(Option<string> option)
            {
                var supplied = parse.GetValue(option)?.Trim();
                if (string.IsNullOrWhiteSpace(supplied) && interactive)
                { prompted = true; supplied = terminal.Prompt(option.Name + " (" + option.Description + "): ")?.Trim(); }
                return supplied;
            }
        });
        return command;
    }
}
