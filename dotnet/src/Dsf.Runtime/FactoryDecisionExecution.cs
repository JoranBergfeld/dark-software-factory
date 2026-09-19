using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal sealed class FactoryDecisionExecution
{
    private readonly PublisherAuthoritySnapshot publisher;
    private readonly FactoryPublisherAuthority authority;
    private readonly FactoryPromotionApproval promotion;
    private readonly FactoryPromotionSeeds seeds;
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    internal FactoryPublicationDecisionScope Scope { get; }
    internal FactoryActivityStore Decisions { get; }
    internal string ProblemScope => FactoryPromotion.ProblemScope(publisher.Manifest.Identity.FactoryId, publisher.Manifest.Identity.RepositoryId);

    private FactoryDecisionExecution(PublisherAuthoritySnapshot publisher, FactoryPublisherAuthority authority,
        FactoryPromotionApproval promotion, FactoryPromotionSeeds seeds, FactoryDecisionRequest request, HttpClient http, TimeProvider clock)
    {
        this.publisher = publisher; this.authority = authority; this.promotion = promotion; this.seeds = seeds; this.http = http; this.clock = clock;
        Scope = new(request.InvocationId, publisher.Identity(FactoryPromotion.RootKey), publisher.Identity(FactoryPublisher.RootKey), request.DryRun);
        Decisions = new(publisher.Manifest, promotion, false, seeds.Decisions.Identity, publisher.Credential, http, clock);
    }

    internal static async Task<FactoryDecisionExecution> AdmitAuthorityAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        FactoryDecisionRequest request, TokenCredential credential, HttpClient http, CancellationToken token, bool execute = false)
    {
        if (request.InvocationId == Guid.Empty) throw new JsonException("Original explicit decision invocation required.");
        var authority = new FactoryPublisherAuthority(settings, true, credential, http, dependencies.PreviewClock);
        var publisher = await authority.CheckAsync(token);
        var promotion = FactoryPromotion.RequireApproved(publisher.Records.ToDictionary(p => p.Key, p => p.Value.Value),
            publisher.Records.Keys.ToDictionary(k => k, publisher.Identity), publisher.Capability.Plan, dependencies.PreviewClock.GetUtcNow());
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(publisher.Records[promotion.NativeIndices!.Key].Value,
            FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var execution = new FactoryDecisionExecution(publisher, authority, promotion, seeds, request, http, dependencies.PreviewClock);
        _ = await execution.Decisions.ReadAsync(token);
        await execution.Decisions.RequireSettledAsync(FactoryPromotion.DecisionId(publisher.Manifest.Identity.FactoryId, request.InvocationId), token);
        await execution.Publications().RequireSettledAsync(request.InvocationId.ToString("N"), token);
        if (execute) await execution.RequireNativeProviderAsync(token);
        return execution;
    }

    private async Task RequireNativeProviderAsync(CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var plan = publisher.Capability.Plan;
        using var request = new HttpRequestMessage(HttpMethod.Post, publisher.Capability.PublisherEndpoint + "/capability")
        { Content = JsonContent.Create(new FactoryLiveRequest(plan.Selection.OperationId, plan.Fingerprint), options: FactorySourceValidation.RecordJson) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await publisher.Credential.GetTokenAsync(
            new TokenRequestContext(["api://" + plan.Selection.AudienceClientId + "/.default"]), pending.Token)).Token);
        using var response = await http.SendAsync(request, pending.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentNativePublicationProviderOrAutomationUnavailable");
        using var body = await EvidencePayload.ReadAsync(response, pending.Token, 64 * 1024);
        var proof = body.RootElement.Deserialize<FactoryPublisherObservation>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        FactoryPublisher.ValidateObservation(proof, plan, clock.GetUtcNow() - FactoryPreviewValidation.RequestBudget, clock.GetUtcNow());
        if (proof.AuthorityRevision != publisher.Revision) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentNativePublicationAuthorityChanged");
        await RequireCurrentAsync(token);
    }

    internal async Task<FactoryLiveExecution> LiveAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        TokenCredential credential, CancellationToken token)
    {
        var authority = new FactoryLiveAuthority(settings, dependencies, credential, http);
        var request = new FactoryLiveRequest(Scope.InvocationId, promotion.LiveFingerprint);
        var current = await authority.CheckAsync(request, true, token);
        await RequireCurrentAsync(token);
        return new(current, request, authority, this);
    }

    internal Task RequireCurrentAsync(CancellationToken token) => authority.RequireSameAsync(publisher, token);

    internal IProblemIdentityResolver Problems()
    {
        var factory = publisher.Manifest.Identity;
        var fixedScope = ProblemScope;
        return new FixedScope(new CosmosProblemIdentityResolver("https://" + FactoryPreviewValidation.Prefix(factory.FactoryId)
            + "-cosmos.documents.azure.com", factory.ProductKey, FactoryPreviewValidation.PreviewContainer, factory.ProductKey,
            new AzureCosmosDocumentGateway(publisher.Credential, http), originalResourceId: seeds.Problems.Identity.NativeResourceId,
            beforeMutation: RequireCurrentAsync), fixedScope, RequireCurrentAsync);
    }

    internal async Task<IReadOnlyList<FactoryPublicationResult>> ObservePublicationsAsync(ConveyorRun run, bool dispatch, CancellationToken token)
    {
        if (Scope.DryRun) return [];
        var results = new List<FactoryPublicationResult>();
        foreach (var candidate in run.Proposals.Where(p => p.Verdict == ProposalVerdict.Proceed))
        {
            await RequireCurrentAsync(token);
            if (dispatch)
            {
                using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, publisher.Capability.PublisherEndpoint + "/publish")
                { Content = JsonContent.Create(new FactoryPublicationRequest(Scope.InvocationId, candidate.Id), options: FactorySourceValidation.RecordJson) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await publisher.Credential.GetTokenAsync(
                    new TokenRequestContext(["api://" + publisher.Capability.Plan.Selection.AudienceClientId + "/.default"]), pending.Token)).Token);
                using var response = await http.SendAsync(request, pending.Token);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentAuthenticatedProposalPublicationUnavailable");
                using var document = await EvidencePayload.ReadAsync(response, pending.Token, AzureCosmosDocumentGateway.MaximumCheckpointBytes);
                var result = document.RootElement.Deserialize<FactoryPublicationResult>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (result.Journal.Intent.FactoryId != publisher.Manifest.Identity.FactoryId
                    || result.Journal.Intent.RepositoryId != publisher.Manifest.Identity.RepositoryId
                    || result.Journal.Intent.LogicalIntent != candidate.IntentKey || result.NativeIdentity is null) throw new JsonException();
                results.Add(result);
            }
            else
            {
                var factory = publisher.Manifest.Identity;
                var id = FactoryPublication.Id(factory.FactoryId, factory.RepositoryId, candidate.IntentKey!);
                var index = await Publications().ReadAsync(token);
                var entry = index.Document.Entries.SingleOrDefault(e => e.Id == id);
                if (entry is null) continue;
                if (entry.NativeResourceId is null) throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalPublicationAdmissionUnconfirmed");
                var native = await new AzureCosmosDocumentGateway(publisher.Credential, http).ReadCheckpointAsync("https://"
                    + FactoryPreviewValidation.Prefix(factory.FactoryId) + "-cosmos.documents.azure.com", factory.ProductKey,
                    FactoryPublication.Container, factory.ProductKey, id, token) ?? throw new JsonException();
                var receipt = FactoryActivityStore.Identity(publisher.Manifest, true, id, native);
                if (receipt.NativeResourceId != entry.NativeResourceId) throw new JsonException();
                var journal = FactoryPublication.ReadDocument(native.Json, clock.GetUtcNow());
                results.Add(new(journal.State == FactoryPublicationState.Dispatched ? FactoryPublicationState.Unknown : journal.State, journal)
                    { NativeIdentity = receipt });
            }
        }
        return results;
    }

    private FactoryActivityStore Publications() => new(publisher.Manifest, promotion, true, seeds.Publications.Identity, publisher.Credential, http, clock);
    private sealed class FixedScope(IProblemIdentityResolver inner, string scope, Func<CancellationToken, Task> requireCurrent) : IProblemIdentityResolver
    {
        public async Task<string> ResolveAsync(string ignored, EvidenceCluster cluster, CancellationToken token)
        { await requireCurrent(token); return await inner.ResolveAsync(scope, cluster, token); }
    }
}
