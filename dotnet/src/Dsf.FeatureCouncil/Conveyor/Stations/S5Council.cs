namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>Persists deliberation and independent jury review before allowing routing or filing.</summary>
public sealed class S5Council : IStation
{
    public const string StationName = "s5_council";
    public const double DefaultThreshold = 0.6;
    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (services.DeliberationRounds is < 1 or > 2)
            {
                throw new InvalidOperationException("S5 deliberation rounds must be one or two");
            }

            if (services.JuryTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)))
            {
                throw new InvalidOperationException("S5 jury timeout must be positive and at most ten minutes");
            }

            var jurors = services.ValidationJurors;
            if (jurors.Count != 3 || jurors.Any(juror => juror is null || string.IsNullOrWhiteSpace(juror.Name))
                || jurors.Select(juror => juror.Name).Distinct(StringComparer.Ordinal).Count() != 3)
            {
                throw new InvalidOperationException("S5 requires exactly three distinct, configured validation jurors");
            }

            var threshold = await services.ConfidenceThresholdReader.ReadThresholdAsync(cancellationToken);
            foreach (var proposal in run.Proposals)
            {
                try
                {
                    await ReviewAsync(proposal, run, services, threshold, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    RecordError(proposal, exception);
                    throw;
                }
            }

            if (run.Proposals.Any(proposal => proposal.Verdict == ProposalVerdict.Kill))
            {
                run.Status = RunStatus.Killed;
                run.Record(StationName, "killed: the jury unanimously voted no-go on at least one proposal.");
            }
            else if (run.Proposals.Any(proposal => proposal.Verdict == ProposalVerdict.Escalate))
            {
                run.Status = RunStatus.Escalated;
                run.Record(StationName, "escalated: low creation maturity or split jury requires human review.");
            }

            run.Record(StationName,
                $"council complete: {run.Proposals.Count(p => p.Verdict == ProposalVerdict.Proceed)} of "
                + $"{run.Proposals.Count} proposal(s) proceeding.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (var proposal in run.Proposals.Where(proposal => proposal.Verdict == ProposalVerdict.Pending))
            {
                RecordError(proposal, exception);
            }

            run.Status = RunStatus.Error;
            if (exception is OperationCanceledException)
            {
                throw new InvalidOperationException("S5 model operation timed out", exception);
            }

            throw;
        }
    }

    private static async Task ReviewAsync(
        Proposal proposal, ConveyorRun run, ConveyorServices services, double threshold, CancellationToken cancellationToken)
    {
        var lenses = services.DeliberationLenses;
        var evidence = proposal.ClusterEvidence.Count > 0 ? proposal.ClusterEvidence
            : run.Evidence.Where(item => proposal.SourceKinds.Contains(item.SourceKind)
                && proposal.EvidenceReferences.Contains(item.Reference)).ToArray();
        var jurorNames = services.ValidationJurors.Select(juror => juror.Name).ToArray();
        var models = services.ValidationJurors.Select(juror => juror.Model).OfType<Dsf.Core.Runtime.JurorModelSettings>().ToArray();
        var review = proposal.CouncilReview ?? new CouncilReview
        {
            Evidence = evidence,
            Threshold = threshold,
            ProductMaturity = services.ProductMaturity,
            RoundsRequested = services.DeliberationRounds,
            JurorNames = jurorNames,
            Models = models,
        };
        proposal.CouncilReview = review;
        if (review.Evidence.Count == 0 || !review.Evidence.ToHashSet().SetEquals(evidence)
            || !review.JurorNames.SequenceEqual(jurorNames) || !review.Models.SequenceEqual(models))
        {
            throw new InvalidOperationException("S5 checkpoint evidence or jury configuration is incomplete or changed");
        }

        if (review.JuryVerdicts.Count > 3
            || review.JuryVerdicts.Any(verdict => verdict is null || string.IsNullOrWhiteSpace(verdict.Rationale)
                || !Enum.IsDefined(verdict.Position) || !jurorNames.Contains(verdict.JurorName))
            || review.JuryVerdicts.Select(verdict => verdict.JurorName).Distinct(StringComparer.Ordinal).Count()
                != review.JuryVerdicts.Count)
        {
            throw new InvalidOperationException("S5 checkpoint contains an invalid jury roster or incomplete result");
        }

        if (review.RoundsRequested is < 1 or > 2 || review.Rounds.Count > review.RoundsRequested
            || review.Rounds.Where((round, index) => round.Number != index + 1).Any())
        {
            throw new InvalidOperationException("S5 checkpoint contains invalid deliberation rounds");
        }

        foreach (var round in review.Rounds)
        {
            ValidateRound(round.Verdicts, lenses);
        }

        if (review.Outcome != ProposalVerdict.Pending)
        {
            if (review.Outcome is not (ProposalVerdict.Proceed or ProposalVerdict.Escalate or ProposalVerdict.Kill)
                || review.Rounds.Count != review.RoundsRequested || review.Recommendation is null
                || review.Outcome != JuryVerdictRules.Decide(review.ProductMaturity, review.JuryVerdicts))
            {
                throw new InvalidOperationException("S5 checkpoint has no complete typed council outcome");
            }

            ValidateRecommendation(review);
            proposal.Verdict = JuryVerdictRules.Decide(services.ProductMaturity, review.JuryVerdicts);
            proposal.CouncilReview = review with { Outcome = proposal.Verdict, ProductMaturity = services.ProductMaturity };
            return;
        }

        for (var round = review.Rounds.Count; round < review.RoundsRequested; round++)
        {
            var prior = round == 0 ? [] : review.Rounds[round - 1].Verdicts;
            var verdicts = new List<LensVerdict>();
            foreach (var lens in lenses)
            {
                verdicts.Add(await lens.DeliberateAsync(proposal, run, prior, services.ModelClient, cancellationToken));
            }

            ValidateRound(verdicts, lenses);
            review = review with { Rounds = [.. review.Rounds, new DeliberationRound(round + 1, verdicts.ToArray())] };
            proposal.CouncilReview = review;
            await services.RunStore.SaveAsync(run, StationName, cancellationToken);
        }

        var synthesis = LensSynthesizer.Synthesize(review.Rounds[^1].Verdicts, review.Threshold);
        if (review.Recommendation is not null)
        {
            ValidateRecommendation(review);
        }
        review = review with { Recommendation = synthesis };
        proposal.CouncilReview = review;
        proposal.Confidence = synthesis.Confidence;
        foreach (var verdict in synthesis.Verdicts)
        {
            run.Record(StationName,
                $"proposal '{proposal.Id}' lens '{verdict.LensName}' position={verdict.Position} rationale: {verdict.Rationale}");
        }

        foreach (var juror in services.ValidationJurors)
        {
            if (review.JuryVerdicts.Any(verdict => verdict.JurorName == juror.Name))
            {
                continue;
            }

            JurorVerdict verdict;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(services.JuryTimeout ?? TimeSpan.FromSeconds(120));
                verdict = await juror.ValidateAsync(proposal, run, synthesis, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"juror '{juror.Name}' failed: {exception.Message}", exception);
            }

            if (verdict is null || verdict.JurorName != juror.Name || string.IsNullOrWhiteSpace(verdict.Rationale)
                || !Enum.IsDefined(verdict.Position))
            {
                throw new InvalidOperationException($"juror '{juror.Name}' returned a malformed or incomplete verdict");
            }

            review = review with { JuryVerdicts = [.. review.JuryVerdicts, verdict] };
            proposal.CouncilReview = review;
            await services.RunStore.SaveAsync(run, StationName, cancellationToken);
            run.Record(StationName,
                $"proposal '{proposal.Id}' juror '{verdict.JurorName}' position={verdict.Position} rationale: {verdict.Rationale}");
        }

        proposal.Verdict = JuryVerdictRules.Decide(services.ProductMaturity, review.JuryVerdicts);
        proposal.CouncilReview = review with { Outcome = proposal.Verdict, ProductMaturity = services.ProductMaturity };
        run.Record(StationName,
            $"proposal '{proposal.Id}' confidence={proposal.Confidence:F2} verdict={proposal.Verdict}"
            + (synthesis.Disagreement ? " (lenses disagreed)" : "") + ".");
        await services.RunStore.SaveAsync(run, StationName, cancellationToken);
    }

    private static void ValidateRound(IReadOnlyList<LensVerdict> verdicts, IReadOnlyList<IDeliberationLens> lenses)
    {
        if (verdicts.Count == 0 || verdicts.Count != lenses.Count
            || verdicts.Any(verdict => verdict is null)
            || verdicts.Select(verdict => verdict.LensName).Distinct(StringComparer.Ordinal).Count() != lenses.Count
            || verdicts.Any(verdict => !lenses.Any(lens => lens.Name == verdict.LensName && lens.Weight == verdict.Weight)
                || !Enum.IsDefined(verdict.Position) || string.IsNullOrWhiteSpace(verdict.Rationale)
                || !double.IsFinite(verdict.Weight) || verdict.Weight <= 0))
        {
            throw new InvalidOperationException("S5 received a malformed or incomplete deliberation round");
        }
    }

    private static void ValidateRecommendation(CouncilReview review)
    {
        var calculated = LensSynthesizer.Synthesize(review.Rounds[^1].Verdicts, review.Threshold);
        if (review.Recommendation is not { } recommendation
            || recommendation.Proceed != calculated.Proceed || recommendation.Confidence != calculated.Confidence
            || recommendation.Disagreement != calculated.Disagreement
            || !recommendation.Verdicts.SequenceEqual(calculated.Verdicts))
        {
            throw new InvalidOperationException("S5 checkpoint recommendation does not match its completed lens round");
        }
    }

    private static void RecordError(Proposal proposal, Exception exception)
    {
        proposal.Verdict = ProposalVerdict.Error;
        proposal.CouncilReview = (proposal.CouncilReview ?? new CouncilReview()) with
        {
            Outcome = ProposalVerdict.Error,
            FailureReason = exception.Message,
        };
    }
}
