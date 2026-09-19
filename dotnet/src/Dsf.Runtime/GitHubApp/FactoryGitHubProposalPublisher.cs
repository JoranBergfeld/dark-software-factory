using Azure.Core;
using Dsf.Core.Onboarding;
using System.Text.Json;

namespace Dsf.Runtime.GitHubApp;

internal sealed class FactoryGitHubProposalPublisher(TokenCredential credential, HttpClient http, TimeProvider clock)
{
    internal async Task<FactoryPublicationResult> CreateAsync(FactoryPublicationIntent intent, FactoryGitHubAuthority authority,
        CancellationToken token, Func<CancellationToken, Task>? requireAdmission = null, FactoryActivityStore? discovery = null)
    {
        FactoryPublication.Validate(intent);
        if (intent.FactoryId != authority.FactoryId || intent.RepositoryId != authority.RepositoryId)
            throw new InvalidOperationException("Exact factory and durable repository required.");
        if (requireAdmission is not null) await requireAdmission(token);
        if (discovery is not null)
            await discovery.RegisterAsync(FactoryPublication.Id(intent), FactoryModelsValidation.Hash(intent),
                requireAdmission ?? throw new InvalidOperationException("Indexed publication requires protected current admission."), token, intendedPublication: intent);
        var journal = new FactoryPublicationJournal(intent, credential, http, clock);
        var original = await journal.OpenAsync(token);
        if (discovery is not null)
            await discovery.ConfirmAsync(original.Id, journal.NativeIdentity!.NativeResourceId!, token);
        if (original.State == FactoryPublicationState.Confirmed) return Result(original.State);
        if (original.State is FactoryPublicationState.Dispatched or FactoryPublicationState.Unknown)
            return Result(FactoryPublicationState.Unknown, "Original issued request unconfirmed; no second create.");
        var access = await new FactoryGitHubVerifier(http, credential, clock).AcquireProposalAccessAsync(authority, token);
        var review = await access.ReviewAsync(original.Intent.AutomationReview, token);
        if (requireAdmission is not null) await requireAdmission(token);
        await journal.DispatchAsync(token, review);
        var createIssued = false;
        try
        {
            if (await access.ReviewAsync(original.Intent.AutomationReview, token) != review)
                throw new InvalidOperationException("Current mutation review changed after native dispatch claim.");
            if (requireAdmission is not null) await requireAdmission(token);
            createIssued = true;
            var receipt = await access.CreateAsync(journal.Document.Intent, token);
            await journal.ConfirmAsync(receipt, token);
            return Result(FactoryPublicationState.Confirmed);
        }
        catch (FactoryIssueRejectedException rejection)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                await journal.RejectedAsync(rejection.Status, rejection.RequestId, cleanup.Token);
                return Result(FactoryPublicationState.Rejected, "Native create explicitly rejected without effect.");
            }
            catch (Exception e) when (Unconfirmed(e)) { return Unknown(); }
        }
        catch (Exception e) when (Unconfirmed(e))
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                if (createIssued) await journal.UnknownAsync(cleanup.Token);
                else
                {
                    await journal.NotDispatchedAsync(cleanup.Token);
                    return Result(FactoryPublicationState.NotDispatched,
                        "Pre-create checks failed; original process confirms no GitHub issue request was issued.");
                }
            }
            catch (Exception failure) when (Unconfirmed(failure)) { }
            return Unknown();
        }

        FactoryPublicationResult Unknown() => Result(FactoryPublicationState.Unknown,
            "Original publication dispatch or receipt unconfirmed; retain native identity, never search or retry.");
        FactoryPublicationResult Result(FactoryPublicationState state, string? reason = null) => new(state, journal.Document, reason)
            { NativeIdentity = journal.NativeIdentity };
    }

    private static bool Unconfirmed(Exception e) => e is PreviewCapabilityException or InvalidOperationException or JsonException or HttpRequestException
        or OperationCanceledException or IOException or ArgumentException or KeyNotFoundException or Azure.RequestFailedException;
}
