"""Decision engine - recommendation -> validation jury -> outcome policy.

The enabled critics produce a deterministic *recommendation* (any veto kills;
else the weighted score must clear the per-product threshold). A separate,
model-diverse *validation jury* then reviews that recommendation, and the
deterministic, maturity-gated *outcome policy* maps the jury onto the final
:class:`Verdict` (ACCEPT / ESCALATE / KILL). Offline the jury echoes the
recommendation, so the line behaves exactly as the critics decided until real
models (or a lower maturity dial) introduce escalation.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import (
    consensus_bar,
    critic_enabled,
    maturity_level,
    threshold,
    weights,
)
from dsf.contracts.models import AuditRecord, CouncilVerdict
from dsf.council.charter_context import load_charter
from dsf.council.critics import ALL_CRITICS
from dsf.council.deliberation import GATE_NAMES, deliberate
from dsf.council.jury import convene_jury
from dsf.council.outcome import decide_outcome
from dsf.council.scope import annotate_scope

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run


async def _recommend(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Synthesize a recommendation from the deterministic gates and the lenses.

    The gates (grounding, duplication) run as deterministic checks that can veto;
    the lenses (value, cost, feasibility, security, strategic fit) deliberate via
    :func:`dsf.council.deliberation.deliberate`. Their positions are folded by
    :meth:`CouncilVerdict.from_scores`: any veto kills, else the weighted mean of
    the enabled gate and lens scores must clear the per-product threshold. Offline
    the lenses fall back to their critics, so the synthesis is identical to the
    pre-deliberation critic loop. A readable per-score summary is attached for the
    audit log.
    """
    product = proposal.product

    enabled_gates = [
        name for name in GATE_NAMES if critic_enabled(services.config, name, product=product)
    ]
    gate_scores = [await ALL_CRITICS[name](proposal, run, services) for name in enabled_gates]
    lens_scores = await deliberate(proposal, run, services)

    scores = gate_scores + lens_scores
    scored_names = [s.critic for s in scores]

    recommendation = CouncilVerdict.from_scores(
        proposal.id,
        scores,
        threshold(services.config, product=product),
        weights(services.config, scored_names),
    )
    vetoes = [s.critic for s in scores if s.veto]
    recommendation.rationale = (
        f"{recommendation.rationale} "
        f"Gates ({len(gate_scores)}) + lenses ({len(lens_scores)}): "
        + ", ".join(f"{s.critic}={s.score:.2f}{'[VETO]' if s.veto else ''}" for s in scores)
        + (f". Vetoes: {', '.join(vetoes)}." if vetoes else ".")
    )
    return recommendation


async def decide(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Decide a proposal: critic recommendation, jury validation, outcome gate.

    The final verdict (ACCEPT / ESCALATE / KILL) comes from the maturity-gated
    outcome policy over the validation jury, not directly from the critics.
    """
    product = proposal.product
    recommendation = await _recommend(proposal, run, services)
    jury = await convene_jury(recommendation, proposal, run, services)
    verdict, outcome_rationale = decide_outcome(
        jury,
        maturity=maturity_level(services.config, product=product),
        consensus_bar=consensus_bar(services.config, product=product),
    )

    go = sum(1 for v in jury.votes if v.go)
    verdict = CouncilVerdict(
        proposal_id=proposal.id,
        verdict=verdict,
        weighted_score=recommendation.weighted_score,
        threshold=recommendation.threshold,
        scores=recommendation.scores,
        jury=jury,
        rationale=(
            f"{outcome_rationale} Jury {go}/{len(jury.votes)} to proceed. "
            f"Recommendation: {recommendation.rationale}"
        ),
    )
    await _annotate_scope(proposal, run, services, verdict)
    return verdict


async def _annotate_scope(
    proposal: Proposal, run: Run, services: Services, verdict: CouncilVerdict
) -> None:
    """Advisory non-goal scope check (gated). Never changes the score or veto.

    On a flagged conflict, append a "scope: ..." line to the verdict rationale and
    a ``council:scope`` audit record. Uncharted / no-non-goal proposals are no-ops.
    """
    if not critic_enabled(services.config, "scope", product=proposal.product):
        return
    charter = await load_charter(services, run, proposal.product)
    note = await annotate_scope(proposal, charter, services)
    if note.in_scope:
        return
    verdict.rationale = f"{verdict.rationale} scope: {note.note} (advisory)."
    run.audit.append(
        AuditRecord(station="council:scope", message=f"{proposal.id}: scope {note.note} (advisory)")
    )


__all__ = ["decide"]
