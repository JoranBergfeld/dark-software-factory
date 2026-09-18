using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed record PreviewOperatorEvaluation(PreviewReadiness Readiness, Guid? OperationId, string? ApprovalFingerprint,
    string? Reason, JsonElement? Evaluation = null)
{
    public DateTimeOffset? ObservedAt { get; init; }
    public bool CurrentReady => Readiness == PreviewReadiness.ReadyAwaitingActivation;
    public string NextActor => Readiness switch
    {
        PreviewReadiness.ReadyAwaitingActivation => "Operator: review the proof; activation requires separate approval. Nothing was published.",
        PreviewReadiness.Unknown or PreviewReadiness.Canceled => "Operator: inspect the original operation; uncertain effects cannot be retried or reset.",
        PreviewReadiness.Blocked => "Prerequisite owner: reconcile current authority without replacing the original operation.",
        PreviewReadiness.Queued or PreviewReadiness.Running => "Original runtime worker: continue admitted work; inspection never starts another evaluation.",
        PreviewReadiness.Checking => "Runtime reader: finish current read-only inspection.",
        _ => "Operator: inspect incomplete evaluation and retained evidence; no readiness or activation authority.",
    };
}

public static partial class CliApplication
{
    private static async Task<int> InvokePreviewEvaluationAsync(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer,
        IAzureCliRunner? azure, HttpClient? http, TimeProvider? clock, string? endpoint, string? product, string? tenant,
        string? subscription, string? operation, string? approval, bool readOnly, bool conflicting, bool interactive, CancellationToken token)
    {
        PreviewOperatorEvaluation result;
        Guid? operationId = null;
        try
        {
            if (conflicting) throw new ReservationStoreException(FactoryReservationState.Blocked, "ConflictingPreviewEvaluationMode; no provider access.");
            var owner = FactoryGovernance.NormalizeEndpoint(Input(endpoint, "--owner-appconfig-endpoint"));
            var key = Input(product, "--product");
            var tenantId = Identifier(tenant, "--tenant").ToString();
            var subscriptionId = Identifier(subscription, "--subscription").ToString();
            operationId = Identifier(operation, "--operation");
            approval = Input(approval, "--approve");
            if (operationId == Guid.Empty || !FactorySourceValidation.Hash(approval))
                throw new ReservationStoreException(FactoryReservationState.Blocked, "ExactConfiguredOperationAndApprovalRequired; no provider access.");
            var time = clock ?? TimeProvider.System;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
                FactoryPreviewPublication.Policy.RequestSeconds + FactoryPreviewExecution.Policy.RequestSeconds), time);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var runner = azure ?? new SystemAzureCliRunner();
            var reviews = reviewer ?? RepositoryAttachmentReviewer.Create(runner);
            var transport = http ?? PreviewPreparationHttp;
            var admission = await new FactoryReservationService(reviews, runner, transport, time)
                .StatusAsync(owner, tenantId, subscriptionId, key, pending.Token);
            result = await new FactoryPreviewPreparationService(reviews, runner, transport, time)
                .EvaluateAsync(admission, operationId.Value, approval, readOnly, pending.Token);
        }
        catch (ReservationStoreException e)
        {
            result = new(e.State == FactoryReservationState.Unknown ? PreviewReadiness.Unknown
            : e.State == FactoryReservationState.Canceled ? PreviewReadiness.Canceled : PreviewReadiness.Blocked, operationId, approval, e.Message);
        }
        catch (PreviewCapabilityException e)
        {
            result = new(e.State == FactoryCharterState.Blocked ? PreviewReadiness.Blocked : PreviewReadiness.Unknown,
                operationId, approval, e.Message);
        }
        catch (OperationCanceledException)
        {
            result = new(readOnly ? PreviewReadiness.Canceled : PreviewReadiness.Unknown, operationId, approval,
            "PreviewRequestCanceled; inspect original durable state before continuation.");
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or HttpRequestException or IOException or PreviewCapabilityException
            or CharterAdoptionEvidenceException or CharterOperationException or ModelCapabilityException
            or OwnerReservationStore.AuthenticationFailedException)
        { result = new(PreviewReadiness.Unknown, operationId, approval, "PreviewEvaluationAuthorityOrResponseUnconfirmed; no replay or reset."); }
        terminal.WriteLine(JsonSerializer.Serialize(result, ReviewJson));
        return result.CurrentReady ? Success : result.Readiness == PreviewReadiness.Canceled ? CanceledExitCode : Failure;

        string Input(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) && interactive) value = terminal.Prompt(name + ": ");
            return !string.IsNullOrWhiteSpace(value) ? value.Trim()
                : throw new ReservationStoreException(FactoryReservationState.Blocked, name + " is required; no provider access.");
        }
        Guid Identifier(string? value, string name) => Guid.TryParse(Input(value, name), out var id) && id != Guid.Empty ? id
            : throw new ReservationStoreException(FactoryReservationState.Blocked, name + " requires a nonempty identifier; no provider access.");
    }
}

internal sealed partial class FactoryPreviewPreparationService
{
    internal async Task<PreviewOperatorEvaluation> EvaluateAsync(FactoryReservationResult admission, Guid operation, string approval,
        bool readOnly, CancellationToken token)
    {
        var store = Store(admission);
        FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryPause.Key, token)).Keys);
        FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryPause.HistoryPrefix, token, maximumRecords: 128)).Keys);
        var preparation = await ReadJournalAsync(admission, store, token) ?? throw Unknown("ConfirmedNativePreparationRequired");
        var values = (await store.ReadFactoryAuthorityAsync(token)).ToDictionary(p => p.Key, p => p.Value.Value);
        var manifest = Parse<FactoryManifest>(values[ProductConfigurationKeys.FactoryManifest]);
        var confirmed = FactoryPreviewPublication.ReadConfirmed(manifest, values, clock.GetUtcNow());
        var publication = Parse<PreviewPublicationJournal>(values[FactoryPreviewPublication.RootKey]);
        var capability = Parse<FactoryPreviewCapability>(values[ProductConfigurationKeys.FactoryPreview]);
        if (publication.Phase != PreviewPublicationPhase.ConfiguredAwaitingEvaluation || capability.OperationId != operation
            || capability.ApprovalFingerprint != approval || capability.Configuration.ExecutionRevision is null)
            throw Failure("ExactConfiguredOperationAndCapabilityApprovalRequired");
        await VerifyPublicationAsync(store, confirmed, publication, values, token);
        await RequirePublicationCurrentAsync(admission, preparation, store, token, configuredEvaluation: true);
        var review = PublicationReview(confirmed, values, publication.Seed, final: true);
        var identity = manifest.Identity;
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "api://" + capability.Configuration.AudienceClientId);
        var input = new PreviewCapabilityRequest(operation, approval, Guid.NewGuid());
        var route = readOnly ? "/evaluation" : "/evaluate";
        PreviewHttpObservation observation;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, review.SeedEndpoint + route)
            { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
            using var hop = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, hop.Token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(["api://" + capability.Configuration.AudienceClientId + "/.default"]), pending.Token)).Token);
            using var response = await (http ?? throw Failure("PreviewNativeTransportRequired")).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
            await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, pending.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(pending.Token), new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
            {
                var failure = document.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                return new(failure.State switch
                {
                    FactoryCharterState.Canceled => PreviewReadiness.Canceled,
                    FactoryCharterState.Partial => PreviewReadiness.Partial,
                    FactoryCharterState.Failed => PreviewReadiness.Failed,
                    FactoryCharterState.Blocked => PreviewReadiness.Blocked,
                    _ => PreviewReadiness.Unknown,
                }, operation, approval, failure.Reason.Length <= 256 ? failure.Reason : "PreviewResponseUnconfirmed");
            }
            observation = document.RootElement.Deserialize<PreviewHttpObservation>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (observation.Protocol != 2 || observation.FactoryId != identity.FactoryId || observation.OperationId != operation
                || observation.RequestId != input.RequestId || observation.ApprovalFingerprint != approval || !Enum.IsDefined(observation.Readiness)
                || observation.ObservedAt.Offset != TimeSpan.Zero || observation.ObservedAt > clock.GetUtcNow()
                || observation.ObservedAt < clock.GetUtcNow() - FactoryPreviewValidation.RequestBudget)
                throw Unknown("PreviewObservationResponseBindingChanged");
            if ((response.StatusCode == HttpStatusCode.Accepted)
                != (observation.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking)
                || observation.Readiness == PreviewReadiness.ReadyAwaitingActivation && observation.Evaluation is null)
                throw Unknown("CompleteNativePreviewObservationRequired");
            if (observation.Readiness is not (PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking)
                || readOnly && observation.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running) break;
            route = "/evaluation";
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        if (observation.Evaluation is not { } report)
            return new(observation.Readiness, operation, approval, observation.Reason) { ObservedAt = observation.ObservedAt };
        var readiness = report.GetProperty("readiness").Deserialize<PreviewReadiness>(FactorySourceValidation.RecordJson);
        if (readiness != observation.Readiness) throw Unknown("PreviewObservationReadinessChanged");
        var reported = report.GetProperty("inputs").GetProperty("capability");
        if (!Enum.IsDefined(readiness) || reported.GetProperty("operationId").GetGuid() != operation
            || reported.GetProperty("approvalFingerprint").GetString() != approval
            || report.GetProperty("inputs").GetProperty("manifest").GetProperty("identity").GetProperty("factoryId").GetGuid() != identity.FactoryId)
            throw Unknown("PreviewEvaluationResponseBindingChanged");
        if (readiness == PreviewReadiness.ReadyAwaitingActivation)
        {
            var proof = report.GetProperty("proof").Deserialize<PreviewReadinessProof>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (proof.FactoryId != identity.FactoryId || proof.OperationId != operation || proof.Candidates.Count == 0)
                throw Unknown("PreviewEvaluationProofBindingChanged");
        }
        var result = new PreviewOperatorEvaluation(readiness, operation, approval,
            report.TryGetProperty("reason", out var reason) ? reason.GetString() : null, report.Clone())
        { ObservedAt = observation.ObservedAt };
        try
        {
            await RequirePublicationCurrentAsync(admission, preparation, store, token, configuredEvaluation: true);
            var current = (await store.ReadFactoryAuthorityAsync(token)).ToDictionary(p => p.Key, p => p.Value.Value);
            await VerifyPublicationAsync(store, confirmed, publication, current, token);
        }
        catch (Exception e) when (e is ReservationStoreException or PreviewCapabilityException or JsonException
            or KeyNotFoundException or OperationCanceledException or ModelCapabilityException or CharterOperationException or CharterAdoptionEvidenceException)
        { return result with { Readiness = PreviewReadiness.Unknown, Reason = "FinalPreviewAuthorityUnconfirmed; historical evaluation retained, not current readiness." }; }
        return result;
    }
}
