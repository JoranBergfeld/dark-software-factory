"""Decision engine — aggregate enabled critics into a :class:`CouncilVerdict`.

Runs only the critics enabled for the proposal's product, gathers their
:class:`CriticScore`s, then defers to
:meth:`CouncilVerdict.from_scores` for the decision rule (any veto kills; else
weighted score must clear the per-product threshold). A readable rationale
summarizing vetoes and the score is attached.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import critic_enabled, threshold, weights
from dsf.contracts.models import CouncilVerdict
from dsf.council.critics import ALL_CRITICS

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run


async def decide(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Decide a proposal's fate via the enabled critic council.

    Only critics with ``critic.<name>`` enabled (per the proposal's product)
    participate. The verdict and weighted score come from
    :meth:`CouncilVerdict.from_scores`, with weights resolved for exactly the
    enabled critics.
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

    verdict = CouncilVerdict.from_scores(
        proposal.id,
        scores,
        threshold(services.config, product=product),
        weights(services.config, enabled_names),
    )

    # Augment the base rationale with a per-critic summary for the audit log.
    vetoes = [s.critic for s in scores if s.veto]
    summary = (
        f"{verdict.rationale} "
        f"Critics ({len(scores)} enabled): "
        + ", ".join(f"{s.critic}={s.score:.2f}{'[VETO]' if s.veto else ''}" for s in scores)
        + (f". Vetoes: {', '.join(vetoes)}." if vetoes else ".")
    )
    verdict.rationale = summary
    return verdict


__all__ = ["decide"]
