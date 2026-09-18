using Azure.Core;
using Dsf.Core.Onboarding;
using System.Text.Json;

namespace Dsf.Runtime.GitHubApp;

internal sealed class FactoryGitHubProposalPublisher(TokenCredential credential, HttpClient http, TimeProvider clock)
{
    // This data-plane module is not an activation authority and is deliberately not exposed by a host.
    internal async Task<FactoryPublicationResult> CreateAsync(FactoryPublicationIntent intent, FactoryGitHubAuthority authority,
        CancellationToken token)
    {
        FactoryPublication.Validate(intent);
        if (intent.FactoryId != authority.FactoryId || intent.RepositoryId != authority.RepositoryId)
            throw new InvalidOperationException("Exact factory and durable repository required.");
        var journal = new FactoryPublicationJournal(intent, credential, http, clock);
        var original = await journal.OpenAsync(token);
        if (original.State == FactoryPublicationState.Confirmed) return new(original.State, original);
        if (original.State is FactoryPublicationState.Dispatched or FactoryPublicationState.Unknown)
            return new(FactoryPublicationState.Unknown, original, "Original issued request unconfirmed; no second create.");
        var access = await new FactoryGitHubVerifier(http, credential, clock).AcquireProposalAccessAsync(authority, token);
        var review = await access.ReviewAsync(token);
        await journal.DispatchAsync(token, review);
        var createIssued = false;
        try
        {
            if (await access.ReviewAsync(token) != review)
                throw new InvalidOperationException("Current mutation review changed after native dispatch claim.");
            createIssued = true;
            var receipt = await access.CreateAsync(journal.Document.Intent, token);
            await journal.ConfirmAsync(receipt, token);
            return new(FactoryPublicationState.Confirmed, journal.Document);
        }
        catch (FactoryIssueRejectedException rejection)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                await journal.RejectedAsync(rejection.Status, rejection.RequestId, cleanup.Token);
                return new(FactoryPublicationState.Rejected, journal.Document, "Native create explicitly rejected without effect.");
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
                    return new(FactoryPublicationState.NotDispatched, journal.Document,
                        "Pre-create checks failed; original process confirms no GitHub issue request was issued.");
                }
            }
            catch (Exception failure) when (Unconfirmed(failure)) { }
            return Unknown();
        }

        FactoryPublicationResult Unknown() => new(FactoryPublicationState.Unknown, journal.Document,
            "Original publication dispatch or receipt unconfirmed; retain native identity, never search or retry.");
    }

    private static bool Unconfirmed(Exception e) => e is InvalidOperationException or JsonException or HttpRequestException
        or OperationCanceledException or IOException or ArgumentException or KeyNotFoundException or Azure.RequestFailedException;
}
