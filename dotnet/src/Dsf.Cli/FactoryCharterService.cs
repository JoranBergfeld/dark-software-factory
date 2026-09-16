using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactorySourceService
{
    internal async Task<CharterEvidence> ReviewCharterAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var store = Store(admission);
        var (_, manifest, github, values) = await ReadAsync(admission, store, token);
        var (source, effects) = FactorySourceValidation.Read(manifest, github, values);
        await CheckNativeAsync(admission, source, manifest, github, values, effects, token);
        var root = await store.ReadAsync(ProductConfigurationKeys.FactorySource, token) ?? throw new JsonException();
        var model = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, token) ?? throw new JsonException();
        var credential = new OwnerReservationStore.OperatorCredential(azure, manifest.Identity.TenantId, manifest.Identity.SubscriptionId,
            manifest.Identity.AzureActorId, "api://" + source.Configuration.Selection.AudienceClientId);
        var access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + source.Configuration.Selection.AudienceClientId + "/.default"]), token);
        using var request = new HttpRequestMessage(HttpMethod.Post, source.HostEndpoint + "/charter-review")
            { Content = JsonContent.Create(new IntentRequest(), options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        using var deadline = new CancellationTokenSource(FactoryIntentLimits.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var response = await (http ?? IntentHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, pending.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failed = await response.Content.ReadFromJsonAsync<IntentResult>(FactorySourceValidation.RecordJson, pending.Token);
            throw new CharterOperationException(failed is null ? FactoryCharterState.Unknown
                : Enum.Parse<FactoryCharterState>(failed.State.ToString()), failed?.Detail ?? "Charter review unconfirmed.");
        }
        var evidence = await response.Content.ReadFromJsonAsync<CharterEvidence>(FactorySourceValidation.RecordJson, pending.Token) ?? throw new JsonException();
        if (evidence.Analysis.State != IntentState.DraftReady || evidence.Analysis.Governs
            || evidence.Analysis.Progress is not { Dispatch: null, State: IntentState.DraftReady } progress
            || progress.AnalyzedChunks != progress.AvailableChunks || progress.CheckedChunks != progress.AvailableChunks
            || evidence.Analysis.ModelAuthority is null || evidence.Analysis.Draft is null
            || evidence.Analysis.Claims.Any(c => !c.Grounded || c.Conflicted)
            || evidence.Source.Snapshot.FactoryId != manifest.Identity.FactoryId
            || evidence.Source.Snapshot.RepositoryId != github.RepositoryId
            || evidence.Source.Content is not null && CharterMarkdown.GitBlobSha(evidence.Source.Content) != evidence.Source.Blob)
            throw new JsonException();
        evidence.Analysis.Draft.Validate();
        await CheckNativeAsync(admission, source, manifest, github, values, effects, pending.Token);
        await CurrentAsync(manifest, github, store, root, pending.Token);
        var currentModel = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, pending.Token);
        if (currentModel?.ETag != model.ETag || currentModel.Value != model.Value) throw new JsonException();
        return evidence;
    }
}

internal sealed class CharterOperationException(FactoryCharterState state, string detail) : Exception(detail)
{
    internal FactoryCharterState State { get; } = state;
}

internal sealed partial class FactoryCharterService(RepositoryAttachmentReviewer reviewer, IAzureCliRunner azure,
    HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryCharterResult> PreviewAsync(FactoryReservationResult admission, bool existing, CancellationToken token)
    {
        var selection = admission.Plan!.Selection;
        var boundary = await reviewer.ReviewAsync(new(selection.Repository, selection.OwnerAppConfigEndpoint, selection.OwnerKeyVaultUri)
        {
            DurableRepositoryId = admission.RepositoryId, AutomationDeclarations = selection.AutomationDeclarations,
            AutomationOwner = selection.AutomationOwner,
        }, token);
        if (boundary.Repository is not { } repository || repository.Id != admission.RepositoryId || boundary.Operator is null
            || boundary.Findings.Any(f => f.State != RepositoryReviewState.Confirmed)
            || boundary.Automation.SingleOrDefault(a => a.Mutation == RepositoryMutationClass.CharterPullRequest)?.State != RepositoryReviewState.Confirmed)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Current repository identity, consent, labels and charter-write automation boundary required; no repairs.");
        var evidence = await new FactorySourceService(azure, http, clock).ReviewCharterAsync(admission, token);
        var source = evidence.Source;
        if (source.Snapshot.Repository != repository.Owner + "/" + repository.Name || source.Snapshot.DefaultBranch != repository.DefaultBranch)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Current default branch/source differs; repeat source review.");
        var after = OnboardingCharterMarkdown.Render(evidence.Analysis.Draft!);
        var progress = evidence.Analysis.Progress!;
        if (existing)
        {
            if (source.Content is null)
                throw new CharterOperationException(FactoryCharterState.Blocked, "Current charter missing; previous good charter is history, not current authority.");
            try { _ = OnboardingCharterMarkdown.Parse(source.Content, admission.Identity!.ProductKey); }
            catch (CharterParseException) { throw new CharterOperationException(FactoryCharterState.Blocked, "Current charter invalid; previous good charter is history."); }
            if (source.Snapshot.Id != progress.Snapshot.Id && source.Content != after)
                throw new CharterOperationException(FactoryCharterState.Blocked,
                    "Human charter differs from the analyzed draft; acquire current source and infer compatible complete analysis before explicit review.");
            after = source.Content;
        }
        var nativeRepository = new OperatorCharterGitHub(reviewer.OperatorGitHub).Get("repositories/" + repository.Id, token);
        var repositoryNodeId = nativeRepository.GetProperty("node_id").GetString();
        if (nativeRepository.GetProperty("id").GetInt64() != repository.Id || repositoryNodeId is not { Length: > 0 and <= 128 })
            throw new JsonException();
        var review = new CharterReview(admission.Identity!.FactoryId, repository.Id, source.Snapshot.Repository, repository.DefaultBranch,
            source.Snapshot.Commit, source.Snapshot.RootTree, CharterMarkdown.CharterPath, source.Content, after, source.Blob, CharterMarkdown.GitBlobSha(after),
            progress.Revision, progress.ChainSha256!, progress.Snapshot.Id, progress.SourceIntegritySha256, progress.ModelConfigurationRevision,
            FactoryModelsValidation.Hash(new { boundary.Repository, boundary.Operator, boundary.App, boundary.Installation, boundary.Labels, boundary.Automation }),
            boundary.Operator.Id, existing, FactoryModelsValidation.Hash(new { evidence.Analysis.Claims, evidence.Analysis.Limitations,
                evidence.Analysis.Chunks, evidence.Analysis.ExcludedPaths }), repositoryNodeId);
        return new(FactoryCharterState.Reviewed, "Exact charter-only before/after; no publication, adoption or activation.", review)
            { Revision = review.Fingerprint, Evidence = evidence };
    }
}
