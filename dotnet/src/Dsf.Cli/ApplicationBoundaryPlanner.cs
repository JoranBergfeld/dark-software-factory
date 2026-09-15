using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed class ApplicationBoundaryPlanner(RepositoryAttachmentReviewer? repositoryReviewer, AzureBoundaryReader azure)
{
    public async Task<ApplicationBoundaryPlan> PlanAsync(
        ApplicationBoundarySelection selection, CancellationToken cancellationToken, string? reviewedFingerprint = null)
    {
        selection = BoundarySelectionInput.Normalize(selection);
        var reviewer = repositoryReviewer ?? RepositoryAttachmentReviewer.Create(azure.ForSubscription(selection.SubscriptionId));
        var repository = await reviewer.ReviewAsync(
            new(selection.Repository, selection.OwnerAppConfigEndpoint, selection.OwnerKeyVaultUri)
            {
                AutomationOwner = selection.AutomationOwner,
                AutomationDeclarations = selection.AutomationDeclarations,
            }, cancellationToken);
        var discovery = await azure.InspectAsync(selection, cancellationToken);
        var admission = await new OwnerAdmissionReader(azure, reviewer).InspectAsync(selection, repository.Repository, cancellationToken);
        var categories = BoundaryPlanCategoriesBuilder.Build(selection, repository, admission);
        var selectionFingerprint = BoundaryPlanFingerprint.Hash(new
        {
            SchemaVersion = 1,
            Selection = selection with { BoundaryReview = null },
            Repository = new
            {
                repository.SchemaVersion, repository.Repository, repository.Operator, repository.App,
                repository.Installation, repository.Labels, repository.Automation, repository.Findings,
            },
            Discovery = discovery, Admission = admission, Categories = categories,
        });
        List<BoundaryPlanFinding> findings =
        [
            .. discovery.Findings, .. admission.Findings,
            .. ApplicationBoundaryPolicy.Review(selection, discovery),
            .. repository.Findings.Where(f => f.State != RepositoryReviewState.Confirmed).Select(f =>
                new BoundaryPlanFinding("repository:" + f.Check, BoundaryPlanCategoriesBuilder.Map(f.State), f.Detail,
                    f.NextActor ?? "repository administrator", f.NextAction ?? "Reconcile current repository review.")),
        ];
        if (selection.BoundaryReview is not { } review)
            findings.Add(new("boundary-review", BoundaryPlanState.Partial,
                "Current exact application boundary requires dedication and cross-owner exclusivity review. "
                + "An owner-scoped reservation is not a global lock; absent tags are not dedication proof.",
                "application owner", "Review the exact application resources, separate evidence backends and topology; bind declarations to selectionFingerprint."));
        else if (review.SelectionFingerprint != selectionFingerprint)
            findings.Add(new("stale-boundary-review", BoundaryPlanState.Blocked,
                "Selection, authority or observed state changed. Earlier dedication/exclusivity review is invalid; selection was not widened.",
                "application owner", "Review the new exact selection and current signals, then supply its selectionFingerprint."));
        else if (!review.Dedicated || !review.CrossOwnerExclusive)
            findings.Add(new("boundary-declaration", BoundaryPlanState.Blocked,
                "Dedication and cross-owner exclusivity must both be explicitly reviewed for the current selection; declarations are not global technical proof.",
                "application / cross-owner administrators", "Select dedicated exclusive application/evidence resources; no override or tag repair is offered."));
        var fingerprint = BoundaryPlanFingerprint.Hash(new { selectionFingerprint, Review = selection.BoundaryReview, Findings = findings });
        if (reviewedFingerprint is not null && !fingerprint.Equals(reviewedFingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
            findings.Add(new("stale-plan-review", BoundaryPlanState.Blocked, "The supplied plan fingerprint does not match the current fresh plan.",
                "operator", "Review the changed categorized plan before replay; --yes is not authority."));
        return new(1, DateTimeOffset.UtcNow, selection, repository, findings)
        {
            Azure = discovery, OwnerAdmission = admission, Categories = categories,
            SelectionFingerprint = selectionFingerprint, Fingerprint = fingerprint,
        };
    }
}
