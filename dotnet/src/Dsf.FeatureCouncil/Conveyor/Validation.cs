namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// A validation juror's typed judgment over a lens recommendation to proceed:
/// binary, unlike a deliberation lens's three-way position -- a juror that
/// cannot reach a clear go/no-go is a malformed result (see <see
/// cref="ModelValidationJuror"/>), never a silent abstention.
/// </summary>
public enum JurorPosition
{
    Go,
    NoGo,
}

/// <summary>
/// One juror's stated position on a proposal the lens synthesizer already
/// recommended proceeding with, with the rationale it gave. A pure data record,
/// for the same reason <see cref="LensVerdict"/> is: <see
/// cref="JuryVerdictRules.Decide"/> only ever needs a list of these, never the
/// juror instance that produced them, so a station test can fix a set of
/// verdicts directly instead of driving real jurors.
/// </summary>
public sealed record JurorVerdict(string JurorName, JurorPosition Position, string Rationale);

/// <summary>
/// One member of S5 council's validation jury -- a separately configured
/// roster of model-family/provider-diverse jurors that each review the lens
/// synthesizer's recommendation to proceed with a proposal, independently of
/// each other and of the lenses that produced that recommendation. Only
/// consulted for a proposal the lens synthesizer already recommends proceeding
/// with (<see cref="LensSynthesis.Proceed"/>); a proposal the lenses did not
/// recommend proceeding with is simply <see cref="ProposalVerdict.Rejected"/>,
/// with no jury review needed. The real implementation (<see
/// cref="ModelValidationJuror"/>) reasons over the proposal through its own
/// model client; tests substitute a fixed-position double so a station test can
/// assert unanimity, a split, or a malformed/unavailable juror result without a
/// live model call.
/// </summary>
public interface IValidationJuror
{
    string Name { get; }

    Task<JurorVerdict> ValidateAsync(
        Proposal proposal, ConveyorRun run, LensSynthesis lensSynthesis, CancellationToken cancellationToken);
}

/// <summary>
/// The deterministic rules S5 council's jury phase applies to reach a
/// proposal's final <see cref="ProposalVerdict"/> from the panel's collected
/// verdicts: <c>low</c> creation maturity always escalates, regardless of how
/// the jurors voted (the jurors are still consulted first, so their verdicts
/// are part of the review package an escalation persists); at <c>medium</c>/<c>high</c>
/// maturity, unanimous <see cref="JurorPosition.Go"/> proceeds, unanimous <see
/// cref="JurorPosition.NoGo"/> kills the run, and any split escalates. A pure
/// function of the maturity and the verdicts, so the exact same panel result
/// always reaches the exact same call.
/// </summary>
public static class JuryVerdictRules
{
    public static ProposalVerdict Decide(string productMaturity, IReadOnlyList<JurorVerdict> verdicts)
    {
        if (string.Equals(productMaturity.Trim(), "low", StringComparison.OrdinalIgnoreCase)
            || productMaturity.Trim().Length == 0)
        {
            return ProposalVerdict.Escalate;
        }

        if (verdicts.Count == 0)
        {
            return ProposalVerdict.Escalate;
        }

        if (verdicts.All(verdict => verdict.Position == JurorPosition.Go))
        {
            return ProposalVerdict.Proceed;
        }

        if (verdicts.All(verdict => verdict.Position == JurorPosition.NoGo))
        {
            return ProposalVerdict.Kill;
        }

        return ProposalVerdict.Escalate;
    }
}

/// <summary>
/// A validation juror that reasons over a proposal -- and the lens
/// synthesizer's recommendation to proceed with it -- through its own model
/// client: asks it to answer with a leading <c>GO</c> or <c>NO-GO</c> token
/// followed by a rationale, then deterministically parses that leading token
/// into a <see cref="JurorPosition"/>. Unlike <see cref="ModelDeliberationLens"/>,
/// an answer with no recognizable leading token is not treated as an
/// abstention: it is a malformed juror result, and this juror throws rather
/// than silently passing the run through -- the same is true of the underlying
/// model call itself failing or timing out. Either way, the exception
/// propagates out of S5 council uncaught, and <see
/// cref="Stations.S5Council"/>'s caller (the conveyor line) turns it into an
/// audited, terminal <see cref="RunStatus.Error"/>, exactly like any other
/// station failure -- never a best-effort fallback.
/// </summary>
public sealed class ModelValidationJuror(string name, string focus, IModelClient modelClient) : IValidationJuror
{
    public string Name { get; } = name;

    /// <summary>
    /// The three jurors S5 council's validation jury panel consults when a
    /// composition wires no others in. All three currently reason through the
    /// same <paramref name="modelClient"/> -- the runtime does not yet compose
    /// distinct model-family/provider clients per juror -- but each juror is an
    /// independently constructed instance with its own name and prompt framing,
    /// so a composition that does wire distinct providers in only needs to
    /// change what <see cref="IModelClient"/> each one is built with, not this
    /// port or the verdict rules that consume it.
    /// </summary>
    public static IReadOnlyList<IValidationJuror> Default(IModelClient modelClient) =>
        [
            new ModelValidationJuror(
                "juror-primary", "whether this proposal should actually proceed, reasoning from first principles",
                modelClient),
            new ModelValidationJuror(
                "juror-adversarial",
                "the strongest case against this proposal proceeding, actively looking for a reason to reject it",
                modelClient),
            new ModelValidationJuror(
                "juror-operational",
                "whether the team on the receiving end could realistically deliver and operate this proposal",
                modelClient),
        ];

    public async Task<JurorVerdict> ValidateAsync(
        Proposal proposal, ConveyorRun run, LensSynthesis lensSynthesis, CancellationToken cancellationToken)
    {
        var answer = await modelClient.CompleteAsync(BuildPrompt(proposal, lensSynthesis), cancellationToken);
        var (position, rationale) = ParseAnswer(answer);
        return new JurorVerdict(Name, position, rationale);
    }

    private string BuildPrompt(Proposal proposal, LensSynthesis lensSynthesis) =>
        $"As juror '{Name}', validate {focus}. The proposal is '{proposal.Title}' "
        + $"(evidence: {string.Join(", ", proposal.EvidenceReferences)}). The deliberation lenses recommend "
        + $"proceeding with confidence {lensSynthesis.Confidence:F2}"
        + (lensSynthesis.Disagreement ? " (the lenses disagreed)" : string.Empty)
        + ". Answer with a leading GO or NO-GO followed by a one-sentence rationale.";

    /// <summary>
    /// Parses the model's leading token into a typed position. Unlike a
    /// deliberation lens, there is no abstention to fall back to: an answer
    /// with no recognizable leading <c>GO</c>/<c>NO-GO</c> token is a malformed
    /// juror result, and this throws rather than guessing a position for it.
    /// </summary>
    private (JurorPosition Position, string Rationale) ParseAnswer(string? answer)
    {
        var trimmed = (answer ?? string.Empty).Trim();
        foreach (var (token, position) in LeadingTokens)
        {
            if (trimmed.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmed[token.Length..].TrimStart(':', '-', '.', ',', ' ').Trim();
                return (position, rest.Length == 0 ? trimmed : rest);
            }
        }

        throw new InvalidOperationException(
            $"juror '{Name}' returned a malformed verdict (no leading GO/NO-GO token): '{trimmed}'");
    }

    // "NO-GO"/"NOGO" checked before "GO": StartsWith is a prefix match, so this
    // ordering is not strictly required for correctness, but keeps the more
    // specific tokens first for readability.
    private static readonly (string Token, JurorPosition Position)[] LeadingTokens =
    [
        ("NO-GO", JurorPosition.NoGo),
        ("NOGO", JurorPosition.NoGo),
        ("GO", JurorPosition.Go),
    ];
}
