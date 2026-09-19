using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static Command BuildPublicationCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock)
    {
        var command = new Command("publication", "inspect or conditionally reconcile original proposal issue outcome; never adopt search matches");
        var endpoint = StringOption("--owner-appconfig-endpoint", "trusted owner endpoint");
        var product = StringOption("--product", "reserved product");
        var tenant = StringOption("--tenant", "reserved tenant");
        var subscription = StringOption("--subscription", "reserved subscription");
        var operation = StringOption("--operation", "original proposal operation ID from native decision output");
        var revision = StringOption("--revision", "exact original native journal revision from inspection");
        var attempt = StringOption("--attempt", "original issued attempt GUID from inspection");
        var reconcile = BoolOption("--reconcile", "conditionally retain original dispatch-owned outcome; no GitHub mutation");
        var retry = BoolOption("--retry", "retry only original native known-no-effect outcome after all fresh live gates");
        var json = BoolOption("--json", "structured redacted original outcome; never prompt");
        AddOptions(command, endpoint, product, tenant, subscription, operation, revision, attempt, reconcile, retry, json);
        command.SetAction(async (parse, token) =>
        {
            FactoryPublicationInspection output;
            try
            {
                var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint));
                var key = Input(product);
                var tenantId = Guid.Parse(Input(tenant)).ToString();
                var subscriptionId = Guid.Parse(Input(subscription)).ToString();
                var change = parse.GetValue(reconcile) || parse.GetValue(retry);
                if (parse.GetValue(reconcile) && parse.GetValue(retry)) throw new ArgumentException();
                var request = new FactoryPublicationRecoveryRequest(Input(operation), change ? Input(revision) : parse.GetValue(revision),
                    change ? Guid.Parse(Input(attempt)) : parse.GetValue(attempt) is { } supplied ? Guid.Parse(supplied) : null);
                FactoryPublicationRecovery.Validate(request, change);
                var runner = azure ?? new SystemAzureCliRunner();
                var time = clock ?? TimeProvider.System;
                var transport = http ?? PreviewPreparationHttp;
                using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, time);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var admission = await new FactoryReservationService(reviewer ?? RepositoryAttachmentReviewer.Create(runner), runner, transport, time)
                    .StatusAsync(owner, tenantId, subscriptionId, key, pending.Token);
                if (admission.State != FactoryReservationState.Reserved || admission.Identity is null
                    || admission.Foundation is not { State: FactoryFoundationState.Inactive, ProductEndpoint: not null })
                    throw new InvalidOperationException("Original protected factory authority required.");
                var factory = admission.Identity;
                var store = new OwnerReservationStore(admission.Foundation.ProductEndpoint, tenantId, subscriptionId, runner, transport, factory.AzureActorId);
                var records = await store.ReadNamespaceAsync(FactoryPublisher.Prefix, pending.Token);
                var capability = FactoryPublisher.ReadConfirmed(records.ToDictionary(p => p.Key, p => p.Value.Value), time.GetUtcNow());
                if (capability.Plan.Deployment.Manifest.Identity != factory) throw new JsonException();
                var audience = "api://" + capability.Plan.Selection.AudienceClientId;
                var credential = new OwnerReservationStore.OperatorCredential(runner, tenantId, subscriptionId, factory.AzureActorId, audience);
                var route = parse.GetValue(reconcile) ? "/publication-reconcile" : parse.GetValue(retry) ? "/publication-retry" : "/publication-inspect";
                using var message = new HttpRequestMessage(HttpMethod.Post, capability.ContextEndpoint + route)
                    { Content = JsonContent.Create(request, options: FactorySourceValidation.RecordJson) };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
                    new TokenRequestContext([audience + "/.default"]), pending.Token)).Token);
                using var response = await transport.SendAsync(message, pending.Token);
                await response.Content.LoadIntoBufferAsync(512 * 1024, pending.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    output = new(response.StatusCode == HttpStatusCode.Forbidden ? FactoryPublicationRecoveryState.Blocked : FactoryPublicationRecoveryState.Unknown,
                        "Original native outcome or fresh retry authority unconfirmed. No fallback, replacement or forced retry.") { Operation = request.Operation };
                }
                else
                {
                    output = await response.Content.ReadFromJsonAsync<FactoryPublicationInspection>(FactorySourceValidation.RecordJson, pending.Token) ?? throw new JsonException();
                    if (output.FactoryId != factory.FactoryId || output.RepositoryId != factory.RepositoryId || output.Product != factory.ProductKey
                        || output.Operation != request.Operation || output.Protocol != 1 || output.Workflow != FactoryPublicationRecovery.Protocol
                        || !Enum.IsDefined(output.State)) throw new JsonException();
                }
                var current = await new FactoryReservationService(reviewer ?? RepositoryAttachmentReviewer.Create(runner), runner, transport, time)
                    .StatusAsync(owner, tenantId, subscriptionId, key, pending.Token);
                if (current.Identity != admission.Identity || current.State != FactoryReservationState.Reserved) throw new JsonException();
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            { output = new(FactoryPublicationRecoveryState.Blocked, "Complete explicit locators and original operation/revision/attempt required; no provider writes."); }
            catch (OperationCanceledException)
            { output = new(FactoryPublicationRecoveryState.Canceled, "Canceled; remote outcome may have persisted. Inspect original operation, not rollback."); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or HttpRequestException or IOException
                or ReservationStoreException or OwnerReservationStore.AuthenticationFailedException or KeyNotFoundException)
            { output = new(FactoryPublicationRecoveryState.Unknown, "Original native publication authority or outcome unavailable; no fallback or GitHub search adoption."); }
            terminal.WriteLine(JsonSerializer.Serialize(output, ReviewJson));
            return output.State == FactoryPublicationRecoveryState.Confirmed ? Success
                : output.State == FactoryPublicationRecoveryState.Canceled ? CanceledExitCode : Failure;

            string Input(Option<string> option)
            {
                var value = parse.GetValue(option)?.Trim();
                if (string.IsNullOrEmpty(value) && terminal.Capabilities.IsInteractive && !parse.GetValue(json))
                    value = terminal.Prompt(option.Name + ": ")?.Trim();
                return !string.IsNullOrEmpty(value) ? value : throw new ArgumentException();
            }
        });
        return command;
    }
}
