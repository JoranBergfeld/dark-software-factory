using System.Text;

namespace Dsf.FeatureCouncil.Conveyor;

internal static class OnboardingPublication
{
    internal const string ProposalLabel = "dsf:proposal";

    internal static IssuePreview Preview(Proposal proposal, ConveyorRun run)
    {
        SemanticGrounding.RequireSupported(proposal, run);
        var context = OnboardingEvaluation.Context(run);
        var review = proposal.CouncilReview ?? throw new EvaluationException("CompleteCouncilReviewRequired");
        if (review.Outcome is not (ProposalVerdict.Proceed or ProposalVerdict.Escalate or ProposalVerdict.Kill)
            || review.Outcome != JuryVerdictRules.DecideProposalOnly(review.JuryVerdicts)
            || review.Rounds.Count != review.RoundsRequested || review.Recommendation is null)
            throw new EvaluationException("CompleteCouncilReviewRequired");
        var body = new StringBuilder(proposal.Body);
        body.Append("\n\n## Scope\nEnvironment: ").Append(context.Application.Environment)
            .Append("\nFactory: ").Append(context.FactoryId)
            .Append("\nApplication resources:\n").AppendJoin('\n', context.Application.ApplicationResourceIds)
            .Append("\n\n## Supporting observations\n");
        foreach (var item in proposal.ClusterEvidence)
            body.Append("- ").Append(item.SourceKind).Append(' ').Append(item.Reference).Append(": ").AppendLine(item.Summary);
        body.Append("\n## Material claims and semantic support\n");
        foreach (var claim in proposal.Synthesis!.Claims)
        {
            var check = proposal.Grounding!.Result.Checks.Single(c => c.ClaimId == claim.Id);
            body.Append("- ").Append(claim.Text).Append(" -- ").Append(check.Support).Append(": ").AppendLine(check.Explanation);
            foreach (var citation in check.Citations)
                body.Append("  Evidence: ").Append(citation.Reference).Append(" -- ").AppendLine(citation.Excerpt);
            foreach (var counter in check.Counterevidence)
                body.Append("  Counterevidence: ").Append(counter.Reference).Append(" -- ").AppendLine(counter.Excerpt);
        }
        body.Append("\n## Uncertainty\n");
        foreach (var limitation in proposal.Synthesis.Uncertainties.Concat(context.Intent.Analysis.Limitations)
            .Concat(proposal.Grounding!.Result.Limitations).Distinct(StringComparer.Ordinal))
            body.Append("- ").AppendLine(limitation);
        body.Append("- Deployed/source correlation: ").AppendLine(context.Intent.Analysis.DeployedSourceCorrelation);
        body.Append("\n## Deliberation\n");
        foreach (var round in review.Rounds)
        foreach (var lens in round.Verdicts)
            body.Append("- Round ").Append(round.Number).Append(", ").Append(lens.LensName).Append(": ")
                .Append(lens.Position).Append(" -- ").AppendLine(lens.Rationale);
        body.Append("\n## Independent jury\n");
        foreach (var vote in review.JuryVerdicts)
            body.Append("- ").Append(vote.JurorName).Append(" (").Append(vote.Model!.Family).Append("): ")
                .Append(vote.Position).Append(" -- ").AppendLine(vote.Rationale);
        body.Append("\nDisposition: ").Append(review.Outcome)
            .Append("\nProposal for human consideration only; unassigned, not accepted for delivery.\n");
        if (body.Length > 60000) throw new EvaluationException("PublicationPayloadBudgetExceeded");
        return new(proposal.Title, proposal.IntentKey, [ProposalLabel])
            { Body = body.ToString(), Assignees = [], Disposition = review.Outcome };
    }
}
