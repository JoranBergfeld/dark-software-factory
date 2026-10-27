using System.CommandLine;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildAuditCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("audit", "inspect, retain and export evidence; never authorize activation or infrastructure deletion");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var page = IntOption("--page", "zero-based retained evidence page");
        var export = StringOption("--export", "review or create an owner-only local export directory");
        var verify = StringOption("--verify-export", "reverify protected receipt and current private artifact without writes");
        var approve = StringOption("--approve", "exact current export, retention policy or expiry review fingerprint");
        var policy = BoolOption("--approve-policy", "review or approve the separate 90-day unused-analysis policy");
        var expire = StringOption("--expire-analysis", "review or expire one exact eligible historical analysis revision");
        var json = BoolOption("--json", "structured private audit evidence; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, page, export, verify, approve, policy, expire, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryAuditResult result;
            var interactive = terminal.Capabilities.IsInteractive && !parse.GetValue(json);
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var name = Input(product);
                var tenantId = Guid.Parse(Input(tenant)).ToString();
                var subscriptionId = Guid.Parse(Input(subscription)).ToString();
                var index = parse.GetValue(page) ?? 0;
                if (index is < 0 or >= FactoryAudit.MaximumRecords / FactoryAudit.PageSize) throw new ArgumentException();
                var destination = parse.GetValue(export);
                var verification = parse.GetValue(verify);
                var approval = parse.GetValue(approve);
                var policyReview = parse.GetValue(policy);
                var expiredRevision = parse.GetValue(expire);
                var actions = (destination is not null ? 1 : 0) + (verification is not null ? 1 : 0)
                    + (policyReview ? 1 : 0) + (expiredRevision is not null ? 1 : 0);
                if (actions > 1 || approval is not null && (actions == 0 || verification is not null) || index != 0 && actions != 0)
                    throw new FactoryAuditException(FactoryAuditState.Blocked,
                        "Choose one action. Approval is valid only for export, retention policy or expiry; inspection and verification are read-only.");
                if (destination is not null) _ = FactoryAuditExport.ReviewPath(destination);
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var reservations = new FactoryReservationService(reviewer, runner, http, time);
                var admission = await reservations.StatusAsync(owner, tenantId, subscriptionId, name, token);
                var audit = new FactoryAuditService(runner, http, time);
                var graph = await audit.ReadAsync(admission, token);
                var current = await reservations.StatusAsync(owner, tenantId, subscriptionId, name, token);
                if (current.State != FactoryReservationState.Reserved || current.Identity != admission.Identity)
                    throw new FactoryAuditException(FactoryAuditState.Unknown, "Final owner registry authority unconfirmed; no receipt or disposition.");
                result = FactoryAuditService.Inspect(graph, index);
                if (policyReview) result = await audit.ApprovePolicyAsync(admission, approval, token);
                if (expiredRevision is not null) result = await audit.ExpireAsync(admission, graph, Guid.Parse(expiredRevision), approval, token);
                if (destination is not null || verification is not null)
                {
                    result = await audit.ExportAsync(admission, graph, destination ?? verification!, approval, verification is not null, token);
                    if (interactive && result.State == FactoryAuditState.Reviewed && approval is null)
                    {
                        terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
                        approval = terminal.Prompt("Exact export approval fingerprint (blank stays read-only): ")?.Trim();
                        if (!string.IsNullOrEmpty(approval))
                            result = await audit.ExportAsync(admission, await audit.ReadAsync(admission, token), destination!, approval, false, token);
                    }
                }
            }
            catch (FactoryAuditException e) { result = new(e.State, e.Message) { Records = e.Records }; }
            catch (ReservationStoreException e) { result = new(FactoryAuditState.Unknown, e.Message); }
            catch (OperationCanceledException) { result = new(FactoryAuditState.Canceled, "Canceled; no deletion authorized. Inspect original audit operation."); }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException
                or HttpRequestException or FormatException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or PreviewCapabilityException or CharterAdoptionEvidenceException or OwnerReservationStore.AuthenticationFailedException)
            {
                result = new(FactoryAuditState.Unknown,
                    "Complete native audit graph, operator authority or operation effect unconfirmed; provider details withheld. No valid receipt or further deletion authorized; inspect retained effects.");
            }
            terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
            return result.State is FactoryAuditState.Inspected or FactoryAuditState.Reviewed or FactoryAuditState.Exported or FactoryAuditState.Verified or FactoryAuditState.Expired
                ? Success : result.State == FactoryAuditState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrEmpty(value) && interactive) value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrEmpty(value) ? value : throw new FactoryAuditException(FactoryAuditState.Blocked, option.Name + " is required; no provider access.");
            }
        });
        return command;
    }
}
