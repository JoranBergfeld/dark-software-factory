using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class RepositoryAttachmentReviewer
{
    private IReadOnlyList<RepositoryMutationReview> ReviewAutomation(RepositoryAttachmentReviewRequest request,
        ReviewedGitHubRepository repository, List<RepositoryReviewFinding> findings, CancellationToken token) =>
        new RepositoryAutomationPolicy((path, cancellation) =>
        {
            var response = ReadGitHub(path, cancellation);
            return Task.FromResult(new RepositoryAutomationResponse(response.Status, response.Body));
        }).ReviewAsync(new(request.AutomationOwner, request.AutomationDeclarations), repository, findings, token).GetAwaiter().GetResult();
}
