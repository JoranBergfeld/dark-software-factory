using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal static partial class RuntimeWorkflow
{
    internal static async Task<FactoryPublicationAdmission> QualifyPublicationAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PublisherAuthoritySnapshot publisher, FactoryPublicationRequest request,
        TokenCredential credential, HttpClient http, CancellationToken token)
    {
        FactoryPromotion.Validate(request);
        var values = publisher.Records.ToDictionary(p => p.Key, p => p.Value.Value);
        var identities = publisher.Records.Keys.ToDictionary(k => k, publisher.Identity);
        var promotion = FactoryPromotion.RequireApproved(values, identities, publisher.Capability.Plan, dependencies.PreviewClock.GetUtcNow());
        var liveAuthority = new FactoryLiveAuthority(settings, dependencies, credential, http);
        var liveRequest = new FactoryLiveRequest(request.InvocationId, promotion.LiveFingerprint);
        var live = await liveAuthority.CheckAsync(liveRequest, false, token);
        var factory = publisher.Manifest.Identity;
        var id = FactoryPromotion.DecisionId(factory.FactoryId, request.InvocationId);
        var endpoint = "https://" + FactoryPreviewValidation.Prefix(factory.FactoryId) + "-cosmos.documents.azure.com";
        var gateway = new AzureCosmosDocumentGateway(publisher.Credential, http);
        var execution = new PreviewExecutionStore(live.Preview, credential, http, dependencies.PreviewClock,
            new FactoryLiveExecution(live, liveRequest, liveAuthority),
            new(request.InvocationId, identities[FactoryPromotion.RootKey], identities[FactoryPublisher.RootKey]));
        var run = await execution.ReadAsync(token) ?? throw Denied("OriginalLiveDecisionRequiredNoHistoricalPreviewPromotion");
        var original = await gateway.ReadCheckpointAsync(endpoint, factory.ProductKey, FactoryPreviewValidation.PreviewContainer,
            factory.ProductKey, id, token) ?? throw Denied("OriginalLiveDecisionRequiredNoHistoricalPreviewPromotion");
        using var body = JsonDocument.Parse(original.Json);
        var evaluation = execution.Document;
        if (FactoryModelsValidation.Hash(PreviewEvaluationDocument.ReadNative(body.RootElement)) != FactoryModelsValidation.Hash(evaluation))
            throw Denied("NativeDecisionChangedDuringQualification");
        if (evaluation.Protocol != 1 || evaluation.Id != id || evaluation.FactoryId != factory.FactoryId || evaluation.Product != factory.ProductKey
            || evaluation.Kind != FactoryPromotion.DecisionKind || evaluation.Profile != "OnboardingPreview" || evaluation.Owner == Guid.Empty
            || evaluation.OperationId != live.Preview.Capability.OperationId || evaluation.Phase != PreviewExecutionPhase.Evaluated
            || evaluation.CreatedAt < promotion.ApprovedAt || evaluation.CreatedAt > dependencies.PreviewClock.GetUtcNow()
            || evaluation.Checksum != evaluation.CalculateChecksum() || evaluation.DecisionScope is not { } scope
            || scope.InvocationId != request.InvocationId || scope.Promotion != identities[FactoryPromotion.RootKey] || scope.Publisher != identities[FactoryPublisher.RootKey]
            || evaluation.LiveScope is null || evaluation.LiveScope.RolloutFingerprint != promotion.LiveFingerprint
            || evaluation.LiveScope.OperationId != request.InvocationId
            || evaluation.Admission is not { State: PreviewWorkState.Released, Readiness: PreviewReadiness.ReadyAwaitingActivation }
            || evaluation.PendingRun is not null || evaluation.Failure is not null || evaluation.Inputs is not { } inputs
            || evaluation.FinalInputs is not { } finalInputs || evaluation.Run is not { } receipt || evaluation.Proof is not { } proof
            || evaluation.Dispatches.Count > FactoryPreviewExecution.Policy.MaximumDispatches
            || evaluation.Dispatches.Any(d => d.State != PreviewModelDispatchState.Confirmed || d.Result is null))
            throw Denied("ExactReleasedPromotedLiveDecisionRequired");
        var nativeRun = await gateway.ReadCheckpointAsync(endpoint, factory.ProductKey, "runs", factory.ProductKey, id + "-run", token) ?? throw new JsonException();
        using var runBody = JsonDocument.Parse(nativeRun.Json);
        var runIdentity = NativeIdentity(nativeRun, "runs", id + "-run");
        var evaluationIdentity = NativeIdentity(original, FactoryPreviewValidation.PreviewContainer, id);
        if (receipt.Id != id + "-run" || receipt.ETag != nativeRun.ETag
            || receipt.ResourceId != runIdentity.NativeResourceId || receipt.Checksum != FactoryModelsValidation.Hash(run)
            || receipt.Checksum != runBody.RootElement.GetProperty("onboardingChecksum").GetString()
            || receipt.NativeDocumentSha256 != FactoryModelsValidation.Hash(runBody.RootElement)
            || proof.Protocol != 1 || proof.FactoryId != factory.FactoryId || proof.OperationId != evaluation.OperationId || proof.PreviewId != id
            || proof.CapabilityRevision != live.Preview.Revision || inputs.CapabilityRevision != live.Preview.Revision
            || proof.InputRevision != inputs.Revision || proof.FinalInputRevision != finalInputs.Revision
            || proof.RunChecksum != receipt.Checksum || proof.RunResourceId != receipt.ResourceId || proof.RunETag != receipt.ETag
            || proof.DispatchRevision != FactoryModelsValidation.Hash(evaluation.Dispatches) || proof.CompletedAt < evaluation.CreatedAt
            || proof.CompletedAt > dependencies.PreviewClock.GetUtcNow())
            throw Denied("OriginalNativeDecisionProofChanged");
        var current = await AcquirePreviewInputsAsync(settings, dependencies, inputs.Request, http, token);
        ValidateCapturedInputs(inputs, current.Snapshot, dependencies.PreviewClock.GetUtcNow());
        ValidateCapturedInputs(finalInputs, current.Snapshot, dependencies.PreviewClock.GetUtcNow());
        var candidates = OnboardingReadiness.ValidateComplete(run, PreviewServices(inputs, run, current.Models, execution), evaluation.Dispatches);
        if (FactoryModelsValidation.Hash(candidates) != FactoryModelsValidation.Hash(proof.Candidates)
            || proof.EvaluationRevision != run.EvaluationRevision || proof.EvaluationRevision != FactoryModelsValidation.Hash(inputs.Context)
            || proof.JudgmentRevision != FactoryModelsValidation.Hash(inputs.Context.Judgment))
            throw Denied("CompleteOriginalNativeCouncilProofRequired");
        var candidate = run.Proposals.SingleOrDefault(p => p.Id == request.CandidateId) ?? throw Denied("OriginalDecisionCandidateRequired");
        if (candidate.Verdict != ProposalVerdict.Proceed || candidate.CouncilReview is not { } review
            || JuryVerdictRules.DecideProposalOnly(review.JuryVerdicts) != ProposalVerdict.Proceed
            || review.JuryVerdicts.Count != 3 || review.JuryVerdicts.Any(j => j.Position != JurorPosition.Go))
            throw Denied("OnlyUnanimousGoQualifiesForPublication");
        var payload = candidate.IntendedPublication ?? throw Denied("OriginalUnassignedProposalPayloadRequired");
        if (payload.Disposition != ProposalVerdict.Proceed || payload.Assignees.Count != 0 || !payload.Labels.SequenceEqual(["dsf:proposal"])
            || string.IsNullOrWhiteSpace(candidate.IntentKey) || candidate.IntentKey.Contains(request.InvocationId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || candidate.IntentKey.Contains(request.InvocationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw Denied("StableUnassignedProposalOnlyIntentRequired");
        var latest = await gateway.ReadCheckpointAsync(endpoint, factory.ProductKey, FactoryPreviewValidation.PreviewContainer, factory.ProductKey, id, token);
        var latestRun = await gateway.ReadCheckpointAsync(endpoint, factory.ProductKey, "runs", factory.ProductKey, id + "-run", token);
        if (latest is null || latestRun is null || NativeIdentity(latest, FactoryPreviewValidation.PreviewContainer, id) != evaluationIdentity
            || NativeIdentity(latestRun, "runs", id + "-run") != runIdentity) throw Denied("NativeDecisionChangedDuringQualification");
        var references = new[] { identities[FactoryPromotion.RootKey], identities[FactoryPublisher.RootKey], evaluationIdentity, runIdentity }
            .Concat(promotion.References).Distinct().ToArray();
        var intent = new FactoryPublicationIntent(1, factory.FactoryId, factory.ProductKey, factory.RepositoryId, candidate.IntentKey,
            request.InvocationId, payload.Title, payload.Body ?? throw new JsonException(), inputs.Revision, references)
            { AutomationReview = publisher.Capability.Plan.Automation };
        FactoryPublication.Validate(intent);
        return new(intent, identities[FactoryPromotion.RootKey], identities[FactoryPublisher.RootKey], evaluationIdentity, runIdentity, publisher.Revision);

        AuditRecordIdentity NativeIdentity(CosmosCheckpoint record, string container, string name)
        {
            using var native = JsonDocument.Parse(record.Json);
            return new(factory.FactoryId, container == "runs" ? AuditStoreKind.Run : AuditStoreKind.Preview,
                $"dbs/{factory.ProductKey}/colls/{container}/docs/{name}", record.ETag, FactoryAudit.TextHash(record.Json))
                { NativeResourceId = NativeCosmosResourceId.RequireDocument(native.RootElement.GetProperty("_rid").GetString()) };
        }
        static PreviewCapabilityException Denied(string reason) => new(FactoryCharterState.Blocked, reason);
    }
}
