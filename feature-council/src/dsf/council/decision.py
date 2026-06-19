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
from dsf.contracts.models import CouncilVerdict
from dsf.council.critics import ALL_CRITICS
from dsf.council.jury import convene_jury
from dsf.council.outcome import decide_outcome

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run


async def _recommend(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Deterministic critic recommendation (the pre-jury proposer tier).

    Only critics with ``critic.<name>`` enabled (per the proposal's product)
    participate. The recommendation verdict and weighted score come from
    :meth:`CouncilVerdict.from_scores`, with weights resolved for exactly the
    enabled critics. A readable per-critic summary is attached for the audit log.
    """
    product = proposal.product
    enabled_names = [
        name
        for name in ALL_CRITICS
        if critic_enabled(services.config, name, product=product)
    ]

    scores = []
    for name in enabled_names:
        scores.append(await ALL_CRITICS[name](proposal, run, services))

    recommendation = CouncilVerdict.from_scores(
        proposal.id,
        scores,
        threshold(services.config, product=product),
        weights(services.config, enabled_names),
    )
    vetoes = [s.critic for s in scores if s.veto]
    recommendation.rationale = (
        f"{recommendation.rationale} "
        f"Critics ({len(scores)} enabled): "
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
    return CouncilVerdict(
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


__all__ = ["decide"]
